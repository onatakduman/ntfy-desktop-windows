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
/// Each subscription runs a background loop that streams the ntfy
/// newline-delimited JSON endpoint, reconnecting with exponential backoff on
/// failure and resuming from the last received message ID so nothing published
/// while the app was closed, asleep or offline is missed. New messages are added to
/// <see cref="Messages"/> (on the UI thread) and surfaced via
/// <see cref="MessageReceived"/>.
/// </summary>
public sealed class SubscriptionManager
{
    /// <summary>
    /// ntfy sends a keepalive every 45 s by default. If nothing at all arrives for this
    /// long the connection is presumed dead and is re-established. Without this, a
    /// socket left half-open by sleep/resume or a network switch keeps the topic
    /// "Connected" forever while no messages arrive.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Longest a message is held back while waiting for our own publish response.</summary>
    private static readonly TimeSpan OwnMessageHoldTimeout = TimeSpan.FromSeconds(10);

    private const int MaxRememberedOwnIds = 500;

    private readonly PersistenceService _persistence;
    private readonly DispatcherQueue _dispatcher;

    private readonly HttpClient _streamClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _publishClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    private readonly HashSet<string> _seenMessageIds = new();
    private readonly object _saveGate = new();

    // Own-message filter state (see RaiseOrHold / EndOwnPublish), guarded by _ownGate.
    private readonly object _ownGate = new();
    private readonly HashSet<string> _ownMessageIds = new();
    private readonly Dictionary<string, int> _publishesInFlight = new();
    private readonly List<NtfyMessage> _heldMessages = new();

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

    /// <summary>
    /// Raised on the UI thread for each new "message" event. Messages this client
    /// published itself arrive with <see cref="NtfyMessage.IsOwn"/> set.
    /// </summary>
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

        // Subscriptions saved by older versions have no resume marker yet: start from
        // the newest message already in history (Messages is newest-first).
        foreach (var sub in Subscriptions)
        {
            sub.LastMessageId ??= Messages.FirstOrDefault(m =>
                m.Topic == sub.Topic &&
                ServerCredential.SameServer(m.ServerUrl, sub.ServerUrl) &&
                !string.IsNullOrEmpty(m.Id))?.Id;
        }

        StartAll();
    }

    public void UpdateMaxHistory(int maxHistory)
    {
        _maxHistory = Math.Max(50, maxHistory);
        TrimHistory();
    }

    // --- Subscription lifecycle (call on the UI thread) ---

    public async Task AddSubscriptionAsync(Subscription sub)
    {
        Subscriptions.Add(sub);
        Start(sub);
        await SaveSubscriptionsAsync().ConfigureAwait(false);
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
        // Restart before awaiting: after ConfigureAwait(false) we'd be on a thread-pool
        // thread, and _running is only touched on the UI thread.
        Stop(sub);
        Start(sub);
        await SaveSubscriptionsAsync().ConfigureAwait(false);
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
        _persistence.SaveSubscriptionsAsync(Subscriptions.ToArray());

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
        var sinceId = sub.LastMessageId;
        _ = Task.Run(() => StreamLoopAsync(sub, sinceId, cts.Token));
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

    private async Task StreamLoopAsync(Subscription sub, string? sinceId, CancellationToken token)
    {
        var attempt = 0;

        while (!token.IsCancellationRequested)
        {
            SetStatus(sub, ConnectionStatus.Connecting, null, token);
            var opened = false;
            try
            {
                await StreamOnceAsync(
                    sub,
                    sinceId,
                    id => sinceId = id,
                    () => opened = true,
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus(sub, ConnectionStatus.Error, ex.Message, token);
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            // A connection that was established resets the backoff (so a long-lived
            // stream that drops reconnects promptly); repeated failures back off
            // exponentially, capped at 30 s.
            attempt = opened ? 1 : Math.Min(attempt + 1, 6);
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
        // No final status update here: Stop() already set "Disconnected", and a
        // replacement loop may be running for the same subscription by now.
    }

    private async Task StreamOnceAsync(
        Subscription sub,
        string? sinceId,
        Action<string> recordMessageId,
        Action onOpen,
        CancellationToken token)
    {
        var baseUrl = sub.ServerUrl.TrimEnd('/');
        var url = $"{baseUrl}/{Uri.EscapeDataString(sub.Topic)}/json";
        if (!string.IsNullOrEmpty(sinceId))
        {
            // Resume after the last message we have. Unlike a timestamp this can't
            // skip a message published in the same second, and on startup it fetches
            // whatever was published while the app wasn't running.
            url += $"?since={Uri.EscapeDataString(sinceId)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var auth = sub.BuildAuthorizationHeader() ?? ResolveCredentialHeader(sub.ServerUrl);
        if (auth is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth);
        }

        // Cancelled by Stop() (token) or by the idle watchdog (re-armed on every line).
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(IdleTimeout);

        try
        {
            using var response = await _streamClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (true)
            {
                var line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                if (line is null)
                {
                    // Stream closed by server.
                    break;
                }

                // Any traffic, keepalives included, proves the connection is alive.
                idle.CancelAfter(IdleTimeout);

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
                        onOpen();
                        SetStatus(sub, ConnectionStatus.Connected, null, token);
                        break;
                    case "keepalive":
                        break;
                    case "message":
                        if (!string.IsNullOrEmpty(msg.Id))
                        {
                            recordMessageId(msg.Id);
                        }
                        msg.ServerUrl = sub.ServerUrl;
                        HandleMessage(sub, msg, token);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && idle.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No data from the server for {IdleTimeout.TotalSeconds:0} seconds; reconnecting.");
        }
    }

    private void HandleMessage(Subscription sub, NtfyMessage msg, CancellationToken token)
    {
        _dispatcher.TryEnqueue(() =>
        {
            // The subscription was stopped (removed/edited) after this line was read.
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (!string.IsNullOrEmpty(msg.Id))
            {
                if (!_seenMessageIds.Add(msg.Id))
                {
                    return; // duplicate (e.g. redelivered after reconnect)
                }
                sub.LastMessageId = msg.Id;
            }

            Messages.Insert(0, msg);
            TrimHistory();
            DebouncedSaveMessages();
            RaiseOrHold(msg);
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

    // --- Own-message filter ---
    //
    // ntfy delivers every published message to all subscribers of the topic,
    // including this client, so publishing from the app used to pop a toast for
    // your own message. Messages we publish are now recorded in history but marked
    // IsOwn (no toast, no unread badge). The stream copy can arrive before or after
    // the publish response that tells us its ID, so a message arriving while a
    // publish to the same topic is still in flight is held briefly until the
    // response is in (or OwnMessageHoldTimeout passes).

    private static string TopicKey(string serverUrl, string topic) =>
        $"{serverUrl.Trim().TrimEnd('/').ToLowerInvariant()}|{topic.Trim()}";

    /// <summary>Decides whether a new message is ours; raises it now or holds it. UI thread.</summary>
    private void RaiseOrHold(NtfyMessage msg)
    {
        lock (_ownGate)
        {
            if (!string.IsNullOrEmpty(msg.Id) && _ownMessageIds.Remove(msg.Id))
            {
                msg.IsOwn = true;
            }
            else if (_publishesInFlight.ContainsKey(TopicKey(msg.ServerUrl, msg.Topic)))
            {
                _heldMessages.Add(msg);
                _ = ReleaseHeldLaterAsync(msg);
                return;
            }
        }

        MessageReceived?.Invoke(msg);
    }

    private async Task ReleaseHeldLaterAsync(NtfyMessage msg)
    {
        await Task.Delay(OwnMessageHoldTimeout).ConfigureAwait(false);

        bool release;
        lock (_ownGate)
        {
            release = _heldMessages.Remove(msg);
        }
        if (release)
        {
            _dispatcher.TryEnqueue(() => MessageReceived?.Invoke(msg));
        }
    }

    private string BeginOwnPublish(string serverUrl, string topic)
    {
        var key = TopicKey(serverUrl, topic);
        lock (_ownGate)
        {
            _publishesInFlight[key] = _publishesInFlight.GetValueOrDefault(key) + 1;
        }
        return key;
    }

    /// <summary>
    /// Marks a publish as finished. <paramref name="messageId"/> is the ID from the
    /// publish response (null if the publish failed or the response was unreadable).
    /// </summary>
    private void EndOwnPublish(string key, string? messageId)
    {
        var release = new List<NtfyMessage>();
        lock (_ownGate)
        {
            if (_publishesInFlight.TryGetValue(key, out var count))
            {
                if (count <= 1)
                {
                    _publishesInFlight.Remove(key);
                }
                else
                {
                    _publishesInFlight[key] = count - 1;
                }
            }

            if (!string.IsNullOrEmpty(messageId))
            {
                var own = _heldMessages.FirstOrDefault(m => m.Id == messageId);
                if (own is not null)
                {
                    // Our copy arrived before the response.
                    own.IsOwn = true;
                    _heldMessages.Remove(own);
                    release.Add(own);
                }
                else
                {
                    // Not here yet (or scheduled with a delay): recognise it on arrival.
                    if (_ownMessageIds.Count >= MaxRememberedOwnIds)
                    {
                        _ownMessageIds.Clear();
                    }
                    _ownMessageIds.Add(messageId);
                }
            }

            if (!_publishesInFlight.ContainsKey(key))
            {
                // Nothing of ours is pending on this topic: anything still held is
                // someone else's message.
                foreach (var m in _heldMessages.Where(m => TopicKey(m.ServerUrl, m.Topic) == key).ToList())
                {
                    _heldMessages.Remove(m);
                    release.Add(m);
                }
            }
        }

        if (release.Count > 0)
        {
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var m in release)
                {
                    MessageReceived?.Invoke(m);
                }
            });
        }
    }

    // --- History ---

    /// <summary>Removes only the messages belonging to a specific server+topic.</summary>
    public void ClearMessagesForTopic(string serverUrl, string topic)
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            var m = Messages[i];
            if (m.Topic == topic && ServerCredential.SameServer(m.ServerUrl, serverUrl))
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
    /// Schedules a history write 1.5s after the last change. Must be called on the UI
    /// thread (it snapshots the collections synchronously); the latest pending call
    /// wins, so writes coalesce during a burst. Subscriptions are saved too, since
    /// each one carries the ID of its newest message (the reconnect marker).
    /// </summary>
    private void DebouncedSaveMessages()
    {
        var messages = Messages.ToArray();
        var subscriptions = Subscriptions.ToArray();
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
                    await _persistence.SaveMessagesAsync(messages).ConfigureAwait(false);
                    await _persistence.SaveSubscriptionsAsync(subscriptions).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer save request.
                }
            });
        }
    }

    /// <summary>Persists the current history and subscriptions immediately (e.g. on shutdown).</summary>
    public async Task FlushMessagesAsync()
    {
        lock (_saveGate)
        {
            _saveDebounce?.Cancel();
            _saveDebounce = null;
        }
        await _persistence.SaveMessagesAsync(Messages.ToArray()).ConfigureAwait(false);
        await _persistence.SaveSubscriptionsAsync(Subscriptions.ToArray()).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates a subscription's status on the UI thread. Updates from a stream loop
    /// that has since been stopped (<paramref name="token"/> cancelled) are dropped,
    /// so they can't overwrite the status of the loop that replaced it.
    /// </summary>
    private void SetStatus(Subscription sub, ConnectionStatus status, string? error, CancellationToken token = default)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }
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
        var ownKey = o.NotifySelf ? null : BeginOwnPublish(o.ServerUrl, o.Topic);
        string? publishedId = null;
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
                publishedId = await ReadMessageIdAsync(response).ConfigureAwait(false);
                return (true, null);
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (false, $"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (ownKey is not null)
            {
                EndOwnPublish(ownKey, publishedId);
            }
        }
    }

    /// <summary>The publish response is the message JSON; returns its ID, or null.</summary>
    private static async Task<string?> ReadMessageIdAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var id = JsonSerializer.Deserialize<NtfyMessage>(body)?.Id;
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not read publish response: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Convenience publish that resolves auth from a matching subscription (and
    /// otherwise saved credentials). Used by the quick compose bar and the
    /// "Send test notification" menu item (which sets <paramref name="notifySelf"/>).
    /// </summary>
    public Task<(bool Success, string? Error)> PublishQuickAsync(
        string serverUrl,
        string topic,
        string message,
        string? title = null,
        int priority = 3,
        string? tags = null,
        bool notifySelf = false)
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
            NotifySelf = notifySelf,
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
