using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NtfyDesktop.Models;

namespace NtfyDesktop.Services;

/// <summary>
/// Owns all subscription streaming connections and the shared message history.
/// Each active (non-muted) subscription runs a background loop that streams the
/// ntfy newline-delimited JSON endpoint, reconnecting with exponential backoff
/// on failure. New messages are added to <see cref="Messages"/> (on the UI
/// thread) and surfaced via <see cref="MessageReceived"/>.
/// </summary>
public sealed class SubscriptionManager
{
    private readonly PersistenceService _persistence;
    private readonly DispatcherQueue _dispatcher;

    private readonly HttpClient _streamClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _publishClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly HashSet<string> _seenMessageIds = new();
    private readonly object _saveGate = new();

    private CancellationTokenSource? _saveDebounce;
    private int _maxHistory = 1000;

    public SubscriptionManager(PersistenceService persistence, DispatcherQueue dispatcher)
    {
        _persistence = persistence;
        _dispatcher = dispatcher;
    }

    /// <summary>Persisted subscriptions (also the binding source for the UI).</summary>
    public ObservableCollection<Subscription> Subscriptions { get; } = new();

    /// <summary>Message history, newest first.</summary>
    public ObservableCollection<NtfyMessage> Messages { get; } = new();

    /// <summary>Raised on the UI thread for each new "message" event.</summary>
    public event Action<NtfyMessage>? MessageReceived;

    /// <summary>Loads persisted state and starts streaming all active subscriptions.</summary>
    public void Initialize(int maxHistory)
    {
        _maxHistory = Math.Max(50, maxHistory);

        foreach (var sub in _persistence.LoadSubscriptions())
        {
            Subscriptions.Add(sub);
        }

        foreach (var msg in _persistence.LoadMessages())
        {
            Messages.Add(msg);
            if (!string.IsNullOrEmpty(msg.Id))
            {
                _seenMessageIds.Add(msg.Id);
            }
        }

        StartAll();
    }

    public void UpdateMaxHistory(int maxHistory)
    {
        _maxHistory = Math.Max(50, maxHistory);
        TrimHistory();
    }

    // --- Subscription lifecycle ---

    public async Task AddSubscriptionAsync(Subscription sub)
    {
        Subscriptions.Add(sub);
        await SaveSubscriptionsAsync().ConfigureAwait(false);
        Start(sub);
    }

    public async Task RemoveSubscriptionAsync(Subscription sub)
    {
        Stop(sub);
        Subscriptions.Remove(sub);
        await SaveSubscriptionsAsync().ConfigureAwait(false);
    }

    /// <summary>Restarts streaming for a subscription whose config changed.</summary>
    public async Task ApplyEditAsync(Subscription sub)
    {
        Stop(sub);
        await SaveSubscriptionsAsync().ConfigureAwait(false);
        Start(sub);
    }

    /// <summary>
    /// Toggles per-topic mute. Muted topics still stream and record history;
    /// only the Windows toast is suppressed, so streaming is left running.
    /// </summary>
    public async Task SetMutedAsync(Subscription sub, bool muted)
    {
        sub.IsMuted = muted;
        await SaveSubscriptionsAsync().ConfigureAwait(false);
    }

    public Task SaveSubscriptionsAsync() =>
        _persistence.SaveSubscriptionsAsync(Subscriptions);

    public void StartAll()
    {
        foreach (var sub in Subscriptions)
        {
            Start(sub);
        }
    }

    public void StopAll()
    {
        foreach (var cts in _running.Values)
        {
            cts.Cancel();
        }
        _running.Clear();
    }

    private void Start(Subscription sub)
    {
        if (string.IsNullOrWhiteSpace(sub.Topic) || _running.ContainsKey(sub.Id))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _running[sub.Id] = cts;
        _ = Task.Run(() => StreamLoopAsync(sub, cts.Token));
    }

    private void Stop(Subscription sub)
    {
        if (_running.TryGetValue(sub.Id, out var cts))
        {
            cts.Cancel();
            _running.Remove(sub.Id);
        }
        SetStatus(sub, ConnectionStatus.Disconnected, null);
    }

    // --- Streaming ---

    private async Task StreamLoopAsync(Subscription sub, CancellationToken token)
    {
        var attempt = 0;
        long? sinceUnix = null;

        while (!token.IsCancellationRequested)
        {
            SetStatus(sub, ConnectionStatus.Connecting, null);
            try
            {
                await StreamOnceAsync(sub, sinceUnix, ts => sinceUnix = ts, token).ConfigureAwait(false);
                // Clean end of stream (server closed) — reconnect promptly.
                attempt = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus(sub, ConnectionStatus.Error, ex.Message);
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            // Exponential backoff capped at 30s.
            attempt = Math.Min(attempt + 1, 6);
            var delaySeconds = Math.Min(30, (int)Math.Pow(2, attempt));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetStatus(sub, ConnectionStatus.Disconnected, null);
    }

    private async Task StreamOnceAsync(
        Subscription sub,
        long? sinceUnix,
        Action<long> recordTimestamp,
        CancellationToken token)
    {
        var baseUrl = sub.ServerUrl.TrimEnd('/');
        var url = $"{baseUrl}/{Uri.EscapeDataString(sub.Topic)}/json";
        if (sinceUnix is long since)
        {
            // Resume from just after the last seen message on reconnect.
            url += $"?since={since}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var auth = sub.BuildAuthorizationHeader() ?? ResolveCredentialHeader(sub.ServerUrl);
        if (auth is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }

        using var response = await _streamClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                // Stream closed by server.
                break;
            }
            if (line.Length == 0)
            {
                continue;
            }

            NtfyMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<NtfyMessage>(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (msg is null)
            {
                continue;
            }

            switch (msg.Event)
            {
                case "open":
                    SetStatus(sub, ConnectionStatus.Connected, null);
                    break;
                case "keepalive":
                    break;
                case "message":
                    if (msg.Time > 0)
                    {
                        recordTimestamp(msg.Time + 1);
                    }
                    msg.ServerUrl = sub.ServerUrl;
                    HandleMessage(msg);
                    break;
            }
        }
    }

    private void HandleMessage(NtfyMessage msg)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!string.IsNullOrEmpty(msg.Id) && !_seenMessageIds.Add(msg.Id))
            {
                return; // duplicate (e.g. redelivered after reconnect with since=)
            }

            Messages.Insert(0, msg);
            TrimHistory();
            MessageReceived?.Invoke(msg);
            DebouncedSaveMessages();
        });
    }

    private void TrimHistory()
    {
        while (Messages.Count > _maxHistory)
        {
            var last = Messages[Messages.Count - 1];
            Messages.RemoveAt(Messages.Count - 1);
            _seenMessageIds.Remove(last.Id);
        }
    }

    /// <summary>Removes only the messages belonging to a specific server+topic.</summary>
    public void ClearMessagesForTopic(string serverUrl, string topic)
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var m = Messages[i];
            if (m.Topic == topic && m.ServerUrl == serverUrl)
            {
                _seenMessageIds.Remove(m.Id);
                Messages.RemoveAt(i);
            }
        }
        DebouncedSaveMessages();
    }

    /// <summary>Removes a single message from history.</summary>
    public void DeleteMessage(NtfyMessage message)
    {
        if (Messages.Remove(message))
        {
            _seenMessageIds.Remove(message.Id);
            DebouncedSaveMessages();
        }
    }

    public void ClearHistory()
    {
        Messages.Clear();
        _seenMessageIds.Clear();
        DebouncedSaveMessages();
    }

    /// <summary>
    /// Schedules a history write 1.5s after the last message. Must be called on
    /// the UI thread (it snapshots the collection synchronously); the latest
    /// pending call wins, so writes coalesce during a burst.
    /// </summary>
    private void DebouncedSaveMessages()
    {
        var snapshot = Messages.ToArray();
        lock (_saveGate)
        {
            _saveDebounce?.Cancel();
            _saveDebounce = new CancellationTokenSource();
            var token = _saveDebounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1500, token).ConfigureAwait(false);
                    await _persistence.SaveMessagesAsync(snapshot).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer save request.
                }
            });
        }
    }

    /// <summary>Persists the current message history immediately (e.g. on shutdown).</summary>
    public Task FlushMessagesAsync()
    {
        var snapshot = Messages.ToArray();
        return _persistence.SaveMessagesAsync(snapshot);
    }

    private void SetStatus(Subscription sub, ConnectionStatus status, string? error)
    {
        _dispatcher.TryEnqueue(() =>
        {
            sub.Status = status;
            sub.LastError = error;
        });
    }

    // --- Publishing ---

    /// <summary>
    /// Publishes a message with the full set of ntfy options. When a local file
    /// is attached the request is a PUT with the file as the body; otherwise it
    /// is a POST with the message text as the body. Returns (success, error).
    /// </summary>
    public async Task<(bool Success, string? Error)> PublishAsync(PublishOptions o)
    {
        try
        {
            var baseUrl = o.ServerUrl.TrimEnd('/');
            var url = $"{baseUrl}/{Uri.EscapeDataString(o.Topic)}";

            var uploadingFile = !string.IsNullOrWhiteSpace(o.LocalFilePath) && File.Exists(o.LocalFilePath);

            using var request = new HttpRequestMessage(
                uploadingFile ? HttpMethod.Put : HttpMethod.Post, url);

            if (uploadingFile)
            {
                request.Content = new ByteArrayContent(await File.ReadAllBytesAsync(o.LocalFilePath!).ConfigureAwait(false));
                var fname = string.IsNullOrWhiteSpace(o.Filename) ? Path.GetFileName(o.LocalFilePath!) : o.Filename;
                request.Headers.TryAddWithoutValidation("Filename", fname);
                // When the body is a file, the message text travels in a header.
                if (!string.IsNullOrWhiteSpace(o.Message))
                {
                    request.Headers.TryAddWithoutValidation("Message", o.Message);
                }
            }
            else
            {
                request.Content = new StringContent(o.Message ?? string.Empty, Encoding.UTF8);
                if (o.Markdown)
                {
                    // text/markdown is the most reliable markdown signal.
                    request.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("text/markdown");
                }
            }

            void Header(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            Header("Title", o.Title);
            if (o.Priority is >= 1 and <= 5 && o.Priority != 3)
            {
                request.Headers.TryAddWithoutValidation("Priority", o.Priority.ToString());
            }
            Header("Tags", o.Tags);
            if (o.Markdown)
            {
                request.Headers.TryAddWithoutValidation("Markdown", "yes");
            }
            Header("Click", o.Click);
            Header("Attach", o.AttachUrl);
            if (!uploadingFile)
            {
                Header("Filename", o.Filename);
            }
            Header("Email", o.Email);
            Header("Delay", o.Delay);
            Header("Icon", o.Icon);
            Header("Actions", o.Actions);

            var auth = BuildAuthHeader(o.AuthMode, o.Token, o.Username, o.Password)
                ?? ResolveCredentialHeader(o.ServerUrl);
            if (auth is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", auth);
            }

            using var response = await _publishClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (false, $"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Convenience publish that resolves auth from a matching subscription (and
    /// otherwise saved credentials). Used by the quick compose bar.
    /// </summary>
    public Task<(bool Success, string? Error)> PublishQuickAsync(
        string serverUrl, string topic, string message, string? title = null, int priority = 3, string? tags = null)
    {
        var match = Subscriptions.FirstOrDefault(s =>
            ServerCredential.SameServer(s.ServerUrl, serverUrl) && s.Topic == topic);
        return PublishAsync(new PublishOptions
        {
            ServerUrl = serverUrl,
            Topic = topic,
            Message = message,
            Title = title,
            Priority = priority,
            Tags = tags,
            AuthMode = match?.AuthMode ?? AuthMode.None,
            Token = match?.Token,
            Username = match?.Username,
            Password = match?.Password,
        });
    }

    /// <summary>
    /// Looks up a saved Settings credential matching the server and returns its
    /// Authorization header, or null if none applies. Used as a fallback when a
    /// subscription/publish has no auth of its own.
    /// </summary>
    private static string? ResolveCredentialHeader(string serverUrl)
    {
        var creds = App.Settings?.Credentials;
        if (creds is null)
        {
            return null;
        }
        foreach (var c in creds)
        {
            if (ServerCredential.SameServer(c.ServerUrl, serverUrl))
            {
                var header = c.BuildAuthorizationHeader();
                if (header is not null)
                {
                    return header;
                }
            }
        }
        return null;
    }

    private static string? BuildAuthHeader(AuthMode mode, string? token, string? user, string? pass)
    {
        switch (mode)
        {
            case AuthMode.Token when !string.IsNullOrWhiteSpace(token):
                return $"Bearer {token!.Trim()}";
            case AuthMode.UsernamePassword when !string.IsNullOrWhiteSpace(user):
                var raw = $"{user}:{pass}";
                return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            default:
                return null;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
