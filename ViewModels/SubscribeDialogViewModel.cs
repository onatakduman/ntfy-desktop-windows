using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Models;
using NtfyDesktop.Services;

namespace NtfyDesktop.ViewModels;

/// <summary>
/// Backs the "Subscribe to topic" dialog (also used to edit an existing
/// subscription). A single Server/Account picker lets the user choose ntfy.sh,
/// one of their saved accounts (server + auth), or "Use another server…" for a
/// custom server with manual authentication.
/// </summary>
public partial class SubscribeDialogViewModel : ObservableObject
{
    private const string PublicServer = "https://ntfy.sh";

    private readonly SubscriptionManager _manager;
    private readonly Subscription? _editing;
    private readonly List<ServerCredential> _creds;

    public SubscribeDialogViewModel(Subscription? editing = null)
    {
        _manager = App.Subscriptions;
        _editing = editing;
        _creds = App.Settings?.Credentials?.ToList() ?? new List<ServerCredential>();

        // Picker: ntfy.sh, then each saved account, then "Use another server…".
        AccountOptions = new List<string> { "ntfy.sh" };
        foreach (var c in _creds)
        {
            AccountOptions.Add(c.DisplayName);
        }
        AccountOptions.Add("Use another server…");

        if (editing is not null)
        {
            Topic = editing.Topic;
            // Resolve which picker entry the existing subscription corresponds to.
            if (ServerCredential.SameServer(editing.ServerUrl, PublicServer) &&
                editing.AuthMode == AuthMode.None)
            {
                SelectedAccountIndex = 0;
            }
            else
            {
                var idx = _creds.FindIndex(c =>
                    ServerCredential.SameServer(c.ServerUrl, editing.ServerUrl));
                if (idx >= 0)
                {
                    SelectedAccountIndex = idx + 1;
                }
                else
                {
                    SelectedAccountIndex = OtherIndex;
                    ServerUrl = editing.ServerUrl;
                    AuthIndex = (int)editing.AuthMode;
                    Token = editing.Token;
                    Username = editing.Username;
                    Password = editing.Password;
                }
            }
        }
        else
        {
            // Default to the first saved account if there is one, else ntfy.sh.
            SelectedAccountIndex = _creds.Count > 0 ? 1 : 0;
        }
    }

    public bool IsEdit => _editing is not null;

    public string DialogTitle => IsEdit ? "Edit subscription" : "Subscribe to topic";

    public string PrimaryButtonText => IsEdit ? "Save" : "Subscribe";

    /// <summary>Picker entries: "ntfy.sh", each saved account, "Use another server…".</summary>
    public List<string> AccountOptions { get; }

    /// <summary>Index of the "Use another server…" entry (last in the list).</summary>
    private int OtherIndex => _creds.Count + 1;

    [ObservableProperty]
    public partial string Topic { get; set; } = string.Empty;

    /// <summary>0 = ntfy.sh, 1..N = saved account, N+1 = use another server.</summary>
    [ObservableProperty]
    public partial int SelectedAccountIndex { get; set; }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = PublicServer;

    /// <summary>Manual auth mode (only for "Use another server…"): 0=None, 1=Token, 2=User/Pass.</summary>
    [ObservableProperty]
    public partial int AuthIndex { get; set; }

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ValidationError { get; set; }

    /// <summary>True only when "Use another server…" is selected.</summary>
    public bool IsOther => SelectedAccountIndex == OtherIndex;

    // The custom-server URL and manual auth fields only show for "Use another server…".
    public bool ShowServerField => IsOther;

    public bool ShowManualAuth => IsOther;

    public bool ShowTokenField => IsOther && AuthIndex == 1;

    public bool ShowUserPassFields => IsOther && AuthIndex == 2;

    public bool HasError => !string.IsNullOrWhiteSpace(ValidationError);

    partial void OnSelectedAccountIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsOther));
        OnPropertyChanged(nameof(ShowServerField));
        OnPropertyChanged(nameof(ShowManualAuth));
        OnPropertyChanged(nameof(ShowTokenField));
        OnPropertyChanged(nameof(ShowUserPassFields));
    }

    partial void OnAuthIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowTokenField));
        OnPropertyChanged(nameof(ShowUserPassFields));
    }

    /// <summary>Fills the topic with a random, hard-to-guess name (like ntfy's GENERATE NAME).</summary>
    [RelayCommand]
    private void GenerateName()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        }
        Topic = new string(chars);
    }

    partial void OnValidationErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    // Values resolved by Validate() and consumed by CommitAsync().
    private string _pendingServer = PublicServer;
    private string _pendingTopic = string.Empty;
    private AuthMode _pendingAuthMode;
    private string _pendingToken = string.Empty;
    private string _pendingUser = string.Empty;
    private string _pendingPass = string.Empty;

    /// <summary>
    /// Validates the form and prepares the values for <see cref="CommitAsync"/>.
    /// Returns true when valid (the dialog may close), false to keep it open. This
    /// is synchronous and does no sidebar/UI work while the dialog is on screen —
    /// the actual persistence/streaming runs in <see cref="CommitAsync"/> after close.
    /// </summary>
    public bool Validate()
    {
        var topic = (Topic ?? string.Empty).Trim();

        string server;
        var authMode = AuthMode.None;
        var token = string.Empty;
        var user = string.Empty;
        var pass = string.Empty;

        if (SelectedAccountIndex == 0)
        {
            server = PublicServer; // ntfy.sh, anonymous
        }
        else if (SelectedAccountIndex >= 1 && SelectedAccountIndex <= _creds.Count)
        {
            var cred = _creds[SelectedAccountIndex - 1];
            server = cred.ServerUrl.Trim();
            authMode = cred.AuthMode;
            token = cred.Token;
            user = cred.Username;
            pass = cred.Password;
        }
        else
        {
            server = (ServerUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(server) ||
                (!server.StartsWith("http://") && !server.StartsWith("https://")))
            {
                ValidationError = "Server URL must start with http:// or https://.";
                return false;
            }
            authMode = (AuthMode)AuthIndex;
            token = Token ?? string.Empty;
            user = Username ?? string.Empty;
            pass = Password ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(topic))
        {
            ValidationError = "Topic is required.";
            return false;
        }

        _pendingServer = server;
        _pendingTopic = topic;
        _pendingAuthMode = authMode;
        _pendingToken = token;
        _pendingUser = user;
        _pendingPass = pass;
        return true;
    }

    /// <summary>
    /// Persists the subscription (or applies the edit) and starts streaming. Call
    /// only after <see cref="Validate"/> returned true AND the dialog has fully
    /// closed — mutating the sidebar while the dialog is dismissing crashes the
    /// compositor on software-rendered (GPU-less) machines.
    /// </summary>
    public async Task CommitAsync()
    {
        if (_editing is { } existing)
        {
            existing.ServerUrl = _pendingServer;
            existing.Topic = _pendingTopic;
            existing.AuthMode = _pendingAuthMode;
            existing.Token = _pendingToken;
            existing.Username = _pendingUser;
            existing.Password = _pendingPass;
            await _manager.ApplyEditAsync(existing);
        }
        else
        {
            await _manager.AddSubscriptionAsync(new Subscription
            {
                ServerUrl = _pendingServer,
                Topic = _pendingTopic,
                AuthMode = _pendingAuthMode,
                Token = _pendingToken,
                Username = _pendingUser,
                Password = _pendingPass,
            });
        }
    }
}
