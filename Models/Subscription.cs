using System;
using System.Text;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NtfyDesktop.Models;

/// <summary>
/// Runtime connection state for a subscription's streaming connection.
/// </summary>
public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

/// <summary>
/// A single ntfy topic subscription: which server and topic to stream, plus
/// optional authentication. The persisted fields are plain properties; runtime
/// state (connection status, last error) is marked <see cref="JsonIgnoreAttribute"/>.
/// </summary>
public partial class Subscription : ObservableObject
{
    /// <summary>Stable identifier used to correlate persisted data and UI selection.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "https://ntfy.sh";

    [ObservableProperty]
    public partial string Topic { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AuthMode AuthMode { get; set; } = AuthMode.None;

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    /// <summary>
    /// When true, incoming messages are still streamed and recorded to history,
    /// but no Windows toast is raised for them (per-topic mute).
    /// </summary>
    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>
    /// Optional user-chosen display name that overrides the raw topic in the UI.
    /// The real <see cref="Topic"/> is still used for the connection.
    /// </summary>
    [ObservableProperty]
    public partial string? Label { get; set; }

    // --- Runtime-only state (not persisted) ---

    [ObservableProperty]
    [property: JsonIgnore]
    public partial ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;

    [ObservableProperty]
    [property: JsonIgnore]
    public partial string? LastError { get; set; }

    /// <summary>"topic@server" style display label, e.g. "alerts@ntfy.sh".</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var host = ServerUrl;
            if (Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
            }
            return string.IsNullOrWhiteSpace(Topic) ? host : $"{Topic}@{host}";
        }
    }

    [JsonIgnore]
    public string StatusGlyph => Status switch
    {
        ConnectionStatus.Connected => "",   // checkmark
        ConnectionStatus.Connecting => "",  // sync
        ConnectionStatus.Error => "",       // error badge
        _ => "",                            // cancel / disconnected
    };

    /// <summary>The sidebar label: the override if set, otherwise the raw topic.</summary>
    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Topic : Label!;

    partial void OnServerUrlChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnTopicChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayLabel));
    }

    partial void OnLabelChanged(string? value) => OnPropertyChanged(nameof(DisplayLabel));

    partial void OnStatusChanged(ConnectionStatus value) => OnPropertyChanged(nameof(StatusGlyph));

    /// <summary>
    /// Builds the <c>Authorization</c> header value for this subscription, or
    /// <c>null</c> if no auth is configured.
    /// </summary>
    public string? BuildAuthorizationHeader()
    {
        switch (AuthMode)
        {
            case AuthMode.Token when !string.IsNullOrWhiteSpace(Token):
                return $"Bearer {Token.Trim()}";
            case AuthMode.UsernamePassword when !string.IsNullOrWhiteSpace(Username):
                var raw = $"{Username}:{Password}";
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                return $"Basic {b64}";
            default:
                return null;
        }
    }
}
