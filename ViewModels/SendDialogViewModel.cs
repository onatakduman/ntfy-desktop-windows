using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Models;
using NtfyDesktop.Services;

namespace NtfyDesktop.ViewModels;

/// <summary>
/// Backs the "Publish notification" dialog. Covers the full ntfy publish feature
/// set (title, priority, tags, markdown, click, attachments, email, delay) and a
/// "Publish another" mode that keeps the dialog open after sending.
/// </summary>
public partial class SendDialogViewModel : ObservableObject
{
    private readonly SubscriptionManager _manager;

    public SendDialogViewModel(string? server, string? topic)
    {
        _manager = App.Subscriptions;
        ServerUrl = string.IsNullOrWhiteSpace(server)
            ? (string.IsNullOrWhiteSpace(App.Settings.DefaultServerUrl) ? "https://ntfy.sh" : App.Settings.DefaultServerUrl)
            : server;
        Topic = topic ?? string.Empty;

        SelectedSubscription = _manager.Subscriptions
            .FirstOrDefault(s => s.Topic == Topic && ServerCredential.SameServer(s.ServerUrl, ServerUrl));
    }

    public ObservableCollection<Subscription> Subscriptions => _manager.Subscriptions;

    [ObservableProperty]
    public partial Subscription? SelectedSubscription { get; set; }

    partial void OnSelectedSubscriptionChanged(Subscription? value)
    {
        if (value is not null)
        {
            ServerUrl = value.ServerUrl;
            Topic = value.Topic;
        }
    }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "https://ntfy.sh";

    [ObservableProperty]
    public partial string Topic { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MessageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Markdown { get; set; }

    [ObservableProperty]
    public partial string Tags { get; set; } = string.Empty;

    /// <summary>0..4 maps to priority 1..5; index 2 (priority 3) is the default.</summary>
    [ObservableProperty]
    public partial int PriorityIndex { get; set; } = 2;

    // --- "Other features" ---

    [ObservableProperty]
    public partial string ClickUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AttachUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AttachFilename { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Delay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool PublishAnother { get; set; }

    public bool HasLocalFile => !string.IsNullOrWhiteSpace(LocalFilePath);

    public string LocalFileName => HasLocalFile ? Path.GetFileName(LocalFilePath) : string.Empty;

    partial void OnLocalFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasLocalFile));
        OnPropertyChanged(nameof(LocalFileName));
    }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(StatusMessage);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    public void ClearLocalFile() => LocalFilePath = string.Empty;

    /// <summary>
    /// Publishes the message. Returns true if the dialog should close (success and
    /// "Publish another" not set), false to stay open.
    /// </summary>
    public async Task<bool> PublishAsync()
    {
        StatusMessage = null;

        if (string.IsNullOrWhiteSpace(ServerUrl) || string.IsNullOrWhiteSpace(Topic))
        {
            StatusMessage = "Server and topic are required.";
            return false;
        }

        var server = ServerUrl.Trim();
        var topic = Topic.Trim();
        var match = _manager.Subscriptions.FirstOrDefault(s =>
            ServerCredential.SameServer(s.ServerUrl, server) && s.Topic == topic);

        IsBusy = true;
        try
        {
            var (success, error) = await _manager.PublishAsync(new PublishOptions
            {
                ServerUrl = server,
                Topic = topic,
                Message = MessageText,
                Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
                Priority = PriorityIndex + 1,
                Tags = string.IsNullOrWhiteSpace(Tags) ? null : Tags.Trim(),
                Markdown = Markdown,
                Click = string.IsNullOrWhiteSpace(ClickUrl) ? null : ClickUrl.Trim(),
                AttachUrl = string.IsNullOrWhiteSpace(AttachUrl) ? null : AttachUrl.Trim(),
                Filename = string.IsNullOrWhiteSpace(AttachFilename) ? null : AttachFilename.Trim(),
                LocalFilePath = string.IsNullOrWhiteSpace(LocalFilePath) ? null : LocalFilePath,
                Email = string.IsNullOrWhiteSpace(Email) ? null : Email.Trim(),
                Delay = string.IsNullOrWhiteSpace(Delay) ? null : Delay.Trim(),
                AuthMode = match?.AuthMode ?? AuthMode.None,
                Token = match?.Token,
                Username = match?.Username,
                Password = match?.Password,
            });

            if (!success)
            {
                StatusMessage = $"Publish failed: {error}";
                return false;
            }

            if (PublishAnother)
            {
                // Keep the dialog open; clear the per-message fields.
                MessageText = string.Empty;
                Title = string.Empty;
                StatusMessage = $"Published to {topic}.";
                return false;
            }

            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
