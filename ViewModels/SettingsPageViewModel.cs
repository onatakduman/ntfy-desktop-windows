using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Models;
using NtfyDesktop.Services;

namespace NtfyDesktop.ViewModels;

/// <summary>
/// Backs the Settings page. Edits the shared <see cref="AppSettings"/> and
/// persists changes immediately, applying theme/history changes live. Also
/// manages the subscribed-topics list and saved server credentials
/// (Authentication), mirroring the ntfy web settings.
/// </summary>
public partial class SettingsPageViewModel : ObservableObject
{
    private bool _loading;

    public SettingsPageViewModel()
    {
        _loading = true;
        var s = App.Settings;
        DefaultServerUrl = s.DefaultServerUrl;
        NotificationsEnabled = s.NotificationsEnabled;
        MinimizeToTrayOnClose = s.MinimizeToTrayOnClose;
        MaxHistory = s.MaxHistory;
        ThemeIndex = s.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };

        Credentials = new ObservableCollection<ServerCredential>(s.Credentials);
        CredServer = s.DefaultServerUrl;

        SoundLabels = new ObservableCollection<string>(NotificationService.Sounds.Select(x => x.Label));
        NotificationSoundIndex = IndexOfSoundKey(s.NotificationSound);
        MinPriorityIndex = System.Math.Clamp(s.MinNotificationPriority - 1, 0, 4);
        _loading = false;
    }

    // --- Notification sound + minimum priority ---

    /// <summary>Display labels for the notification-sound dropdown.</summary>
    public ObservableCollection<string> SoundLabels { get; }

    /// <summary>Index into <see cref="NotificationService.Sounds"/>.</summary>
    [ObservableProperty]
    public partial int NotificationSoundIndex { get; set; }

    /// <summary>0..4 maps to minimum priority 1..5.</summary>
    [ObservableProperty]
    public partial int MinPriorityIndex { get; set; }

    private static int IndexOfSoundKey(string? key)
    {
        for (var i = 0; i < NotificationService.Sounds.Count; i++)
        {
            if (string.Equals(NotificationService.Sounds[i].Key, key, System.StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return 0;
    }

    partial void OnNotificationSoundIndexChanged(int value)
    {
        if (value < 0 || value >= NotificationService.Sounds.Count)
        {
            return;
        }
        App.Settings.NotificationSound = NotificationService.Sounds[value].Key;
        Persist();
    }

    partial void OnMinPriorityIndexChanged(int value)
    {
        App.Settings.MinNotificationPriority = System.Math.Clamp(value, 0, 4) + 1;
        Persist();
    }

    [RelayCommand]
    private void PreviewSound()
    {
        if (NotificationSoundIndex >= 0 && NotificationSoundIndex < NotificationService.Sounds.Count)
        {
            App.Notifications.PreviewSound(NotificationService.Sounds[NotificationSoundIndex].Key);
        }
    }

    /// <summary>The subscribed topics, shown as a management list.</summary>
    public ObservableCollection<Subscription> Subscriptions => App.Subscriptions.Subscriptions;

    public bool HasSubscriptions => Subscriptions.Count > 0;

    // --- Authentication (saved server credentials) ---

    public ObservableCollection<ServerCredential> Credentials { get; }

    public bool HasCredentials => Credentials.Count > 0;

    [ObservableProperty]
    public partial bool IsAddingCredential { get; set; }

    [ObservableProperty]
    public partial string CredServer { get; set; } = "https://ntfy.sh";

    /// <summary>0 = Access token, 1 = Username/Password.</summary>
    [ObservableProperty]
    public partial int CredAuthIndex { get; set; } = 1;

    [ObservableProperty]
    public partial string CredToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CredUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CredPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? CredError { get; set; }

    /// <summary>The credential currently being edited (null when adding a new one).</summary>
    private ServerCredential? _editingCredential;

    public string CredFormTitle => _editingCredential is null ? "Add user" : "Edit user";

    public bool ShowCredToken => CredAuthIndex == 0;

    public bool ShowCredUserPass => CredAuthIndex == 1;

    public bool HasCredError => !string.IsNullOrWhiteSpace(CredError);

    partial void OnCredAuthIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowCredToken));
        OnPropertyChanged(nameof(ShowCredUserPass));
    }

    partial void OnCredErrorChanged(string? value) => OnPropertyChanged(nameof(HasCredError));

    [RelayCommand]
    private void BeginAddCredential()
    {
        _editingCredential = null;
        CredServer = string.IsNullOrWhiteSpace(DefaultServerUrl) ? "https://ntfy.sh" : DefaultServerUrl;
        CredAuthIndex = 1;
        CredToken = string.Empty;
        CredUsername = string.Empty;
        CredPassword = string.Empty;
        CredError = null;
        OnPropertyChanged(nameof(CredFormTitle));
        IsAddingCredential = true;
    }

    [RelayCommand]
    private void EditCredential(ServerCredential? cred)
    {
        if (cred is null)
        {
            return;
        }
        _editingCredential = cred;
        CredServer = cred.ServerUrl;
        CredAuthIndex = cred.AuthMode == AuthMode.Token ? 0 : 1;
        CredToken = cred.Token;
        CredUsername = cred.Username;
        CredPassword = cred.Password;
        CredError = null;
        OnPropertyChanged(nameof(CredFormTitle));
        IsAddingCredential = true;
    }

    [RelayCommand]
    private void CancelAddCredential()
    {
        IsAddingCredential = false;
        CredError = null;
    }

    /// <summary>
    /// Validates the credential form (synchronous, no list mutation). Pairs with
    /// <see cref="CommitCredential"/> so the dialog only mutates the bound list after
    /// it has closed — mutating during the dialog dismiss crashes the WARP compositor.
    /// </summary>
    public bool ValidateCredential()
    {
        var server = (CredServer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(server) ||
            (!server.StartsWith("http://") && !server.StartsWith("https://")))
        {
            CredError = "Server URL must start with http:// or https://.";
            return false;
        }

        var mode = CredAuthIndex == 0 ? AuthMode.Token : AuthMode.UsernamePassword;
        if (mode == AuthMode.Token && string.IsNullOrWhiteSpace(CredToken))
        {
            CredError = "Access token is required.";
            return false;
        }
        if (mode == AuthMode.UsernamePassword && string.IsNullOrWhiteSpace(CredUsername))
        {
            CredError = "Username is required.";
            return false;
        }
        CredError = null;
        return true;
    }

    /// <summary>Persists the validated credential. Call only after the dialog closed.</summary>
    public void CommitCredential()
    {
        var server = (CredServer ?? string.Empty).Trim();
        var mode = CredAuthIndex == 0 ? AuthMode.Token : AuthMode.UsernamePassword;

        if (_editingCredential is { } existing)
        {
            existing.ServerUrl = server;
            existing.AuthMode = mode;
            existing.Token = CredToken ?? string.Empty;
            existing.Username = CredUsername ?? string.Empty;
            existing.Password = CredPassword ?? string.Empty;

            var idx = Credentials.IndexOf(existing);
            if (idx >= 0)
            {
                Credentials[idx] = existing;
            }
            _editingCredential = null;
        }
        else
        {
            var cred = new ServerCredential
            {
                ServerUrl = server,
                AuthMode = mode,
                Token = CredToken ?? string.Empty,
                Username = CredUsername ?? string.Empty,
                Password = CredPassword ?? string.Empty,
            };
            Credentials.Add(cred);
            App.Settings.Credentials.Add(cred);
        }

        OnPropertyChanged(nameof(HasCredentials));
        IsAddingCredential = false;
        Persist();
    }

    [RelayCommand]
    private void SaveCredential()
    {
        var server = (CredServer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(server) ||
            (!server.StartsWith("http://") && !server.StartsWith("https://")))
        {
            CredError = "Server URL must start with http:// or https://.";
            return;
        }

        var mode = CredAuthIndex == 0 ? AuthMode.Token : AuthMode.UsernamePassword;
        if (mode == AuthMode.Token && string.IsNullOrWhiteSpace(CredToken))
        {
            CredError = "Access token is required.";
            return;
        }
        if (mode == AuthMode.UsernamePassword && string.IsNullOrWhiteSpace(CredUsername))
        {
            CredError = "Username is required.";
            return;
        }

        if (_editingCredential is { } existing)
        {
            existing.ServerUrl = server;
            existing.AuthMode = mode;
            existing.Token = CredToken ?? string.Empty;
            existing.Username = CredUsername ?? string.Empty;
            existing.Password = CredPassword ?? string.Empty;

            // Refresh the bound row in place.
            var idx = Credentials.IndexOf(existing);
            if (idx >= 0)
            {
                Credentials[idx] = existing;
            }
            _editingCredential = null;
        }
        else
        {
            var cred = new ServerCredential
            {
                ServerUrl = server,
                AuthMode = mode,
                Token = CredToken ?? string.Empty,
                Username = CredUsername ?? string.Empty,
                Password = CredPassword ?? string.Empty,
            };
            Credentials.Add(cred);
            App.Settings.Credentials.Add(cred);
        }

        OnPropertyChanged(nameof(HasCredentials));
        IsAddingCredential = false;
        Persist();
    }

    [RelayCommand]
    private void RemoveCredential(ServerCredential? cred)
    {
        if (cred is null)
        {
            return;
        }
        Credentials.Remove(cred);
        App.Settings.Credentials.RemoveAll(c => c.Id == cred.Id);
        OnPropertyChanged(nameof(HasCredentials));
        Persist();
    }

    [RelayCommand]
    private async Task Unsubscribe(Subscription? sub)
    {
        if (sub is null)
        {
            return;
        }
        App.Subscriptions.ClearMessagesForTopic(sub.ServerUrl, sub.Topic);
        await App.Subscriptions.RemoveSubscriptionAsync(sub);
        OnPropertyChanged(nameof(HasSubscriptions));
    }

    [ObservableProperty]
    public partial string DefaultServerUrl { get; set; } = "https://ntfy.sh";

    [ObservableProperty]
    public partial bool NotificationsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// Bound to the NumberBox (which works in doubles). Coerced to an int when
    /// applied to settings.
    /// </summary>
    [ObservableProperty]
    public partial double MaxHistory { get; set; } = 1000;

    /// <summary>0 = System default, 1 = Light, 2 = Dark.</summary>
    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    public string AppVersion => "Notidesk 1.0";

    partial void OnDefaultServerUrlChanged(string value)
    {
        App.Settings.DefaultServerUrl = string.IsNullOrWhiteSpace(value) ? "https://ntfy.sh" : value.Trim();
        Persist();
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        App.Settings.NotificationsEnabled = value;
        Persist();
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        App.Settings.MinimizeToTrayOnClose = value;
        Persist();
    }

    partial void OnMaxHistoryChanged(double value)
    {
        // NumberBox can briefly report NaN while the user is editing.
        var clamped = double.IsNaN(value) ? 50 : (int)value;
        if (clamped < 50)
        {
            clamped = 50;
        }
        App.Settings.MaxHistory = clamped;
        App.Subscriptions.UpdateMaxHistory(clamped);
        Persist();
    }

    partial void OnThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "Default",
        };
        App.Settings.Theme = theme;
        (App.Window as MainWindow)?.ApplyTheme(theme);
        Persist();
    }

    private void Persist()
    {
        if (_loading)
        {
            return;
        }
        _ = App.Persistence.SaveSettingsAsync(App.Settings);
    }
}
