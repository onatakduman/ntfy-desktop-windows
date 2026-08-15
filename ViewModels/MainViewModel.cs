using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Models;
using NtfyDesktop.Services;

namespace NtfyDesktop.ViewModels;

/// <summary>
/// Central shell view model. Mirrors the ntfy web app: a sidebar of topics
/// ("All notifications" + each subscription) and a main pane showing the
/// selected topic's notifications. Owns the sidebar entries, the current
/// filtered message list, per-topic unread counts, and the toolbar state.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SubscriptionManager _manager;

    public MainViewModel()
    {
        _manager = App.Subscriptions;

        _manager.Subscriptions.CollectionChanged += OnSubscriptionsChanged;
        _manager.Messages.CollectionChanged += OnMessagesChanged;
        _manager.MessageReceived += OnMessageReceived;

        RebuildEntries();
        SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
    }

    /// <summary>Sidebar entries: index 0 is "All notifications", then one per subscription.</summary>
    public ObservableCollection<NavEntry> Entries { get; } = new();

    /// <summary>Messages shown in the main pane (filtered to the selected entry, newest first).</summary>
    public ObservableCollection<NtfyMessage> CurrentMessages { get; } = new();

    [ObservableProperty]
    public partial NavEntry? SelectedEntry { get; set; }

    [ObservableProperty]
    public partial bool HasMessages { get; set; }

    /// <summary>When true, the content area shows Settings instead of the topic feed.</summary>
    [ObservableProperty]
    public partial bool ShowSettings { get; set; }

    public bool ShowContent => !ShowSettings;

    partial void OnShowSettingsChanged(bool value) => OnPropertyChanged(nameof(ShowContent));

    public bool HasSubscriptions => _manager.Subscriptions.Count > 0;

    public bool IsAllSelected => SelectedEntry?.IsAll ?? true;

    public Subscription? SelectedSubscription => SelectedEntry?.Subscription;

    public string SelectedTitle => SelectedEntry?.Title ?? "All notifications";

    public string SelectedSubtitle
    {
        get
        {
            if (SelectedEntry is null || SelectedEntry.IsAll)
            {
                var n = _manager.Subscriptions.Count;
                return n == 1 ? "1 subscription" : $"{n} subscriptions";
            }
            return SelectedEntry.Subtitle;
        }
    }

    /// <summary>True when a real topic (not "All") is selected, enabling topic-specific actions.</summary>
    public bool CanActOnTopic => SelectedSubscription is not null;

    public bool SelectedIsMuted => SelectedSubscription?.IsMuted ?? false;

    /// <summary>The example publish command shown on the empty-topic state.</summary>
    public string ExampleCommand
    {
        get
        {
            var sub = SelectedSubscription;
            if (sub is null)
            {
                return "curl -d \"Hello world\" ntfy.sh/mytopic";
            }
            var baseUrl = sub.ServerUrl.TrimEnd('/');
            var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Authority : baseUrl;
            return $"curl -d \"Backup successful 🎉\" {host}/{sub.Topic}";
        }
    }

    private NavEntry? _lastRealEntry;

    partial void OnSelectedEntryChanged(NavEntry? value)
    {
        // The "Subscribed topics" header is not selectable — bounce the selection
        // back to the last real entry. With a TwoWay SelectedItem binding this also
        // resets the NavigationView's highlight off the header.
        if (value is { IsHeader: true })
        {
            SelectedEntry = _lastRealEntry;
            return;
        }

        _lastRealEntry = value;
        if (value is not null)
        {
            value.UnreadCount = 0;
            ShowSettings = false; // selecting a topic leaves the Settings pane
        }
        RebuildCurrentMessages();
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(SelectedSubscription));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedSubtitle));
        OnPropertyChanged(nameof(CanActOnTopic));
        OnPropertyChanged(nameof(SelectedIsMuted));
        OnPropertyChanged(nameof(ExampleCommand));
        WindowTitleChanged?.Invoke(WindowTitle);
    }

    /// <summary>Window title: the selected topic, or the app name for "All".</summary>
    public string WindowTitle =>
        SelectedSubscription is { } sub ? $"{sub.DisplayLabel} — Notidesk" : "Notidesk";

    /// <summary>Raised when <see cref="WindowTitle"/> should be re-applied to the window.</summary>
    public event System.Action<string>? WindowTitleChanged;

    private void OnSubscriptionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Update the sidebar entries incrementally. Clearing the whole collection
        // while the NavigationView has a bound SelectedItem crashes the control, so
        // we add/remove individual entries and only fully rebuild as a last resort.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                EnsureHeader();
                NavEntry? added = null;
                foreach (Subscription sub in e.NewItems)
                {
                    added = new NavEntry(sub);
                    Entries.Add(added);
                }
                if (added is not null)
                {
                    // Defer selecting the new topic until the NavigationView has
                    // realized its container. Setting the bound SelectedItem to a
                    // just-added item synchronously (mid collection-change) faults
                    // the control. Low priority lets layout settle first.
                    var toSelect = added;
                    App.DispatcherQueue?.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        () =>
                        {
                            if (Entries.Contains(toSelect))
                            {
                                SelectedEntry = toSelect;
                            }
                        });
                }
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (Subscription sub in e.OldItems)
                {
                    var entry = Entries.FirstOrDefault(en => en.Subscription == sub);
                    if (entry is null)
                    {
                        continue;
                    }
                    var wasSelected = SelectedEntry == entry;
                    // Never let the NavigationView's selected item be the one we remove.
                    if (wasSelected)
                    {
                        SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
                    }
                    Entries.Remove(entry);
                }
                RemoveHeaderIfEmpty();
                break;

            default:
                var keep = SelectedSubscription;
                SelectedEntry = Entries.Count > 0 ? Entries[0] : null;
                RebuildEntries();
                SelectedEntry = (keep is not null
                    ? Entries.FirstOrDefault(en => en.Subscription == keep)
                    : null) ?? (Entries.Count > 0 ? Entries[0] : null);
                break;
        }

        OnPropertyChanged(nameof(HasSubscriptions));
        OnPropertyChanged(nameof(SelectedSubtitle));
    }

    /// <summary>Inserts the "Subscribed topics" header after "All" if it isn't present.</summary>
    private void EnsureHeader()
    {
        if (!Entries.Any(en => en.IsHeader))
        {
            Entries.Insert(Entries.Count > 0 ? 1 : 0, NavEntry.CreateHeader("Subscribed topics"));
        }
    }

    /// <summary>Removes the section header when no topic entries remain.</summary>
    private void RemoveHeaderIfEmpty()
    {
        if (!Entries.Any(en => en.IsTopic))
        {
            var header = Entries.FirstOrDefault(en => en.IsHeader);
            if (header is not null)
            {
                Entries.Remove(header);
            }
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildCurrentMessages();
    }

    private void OnMessageReceived(NtfyMessage message)
    {
        // Bump the unread badge for topics that aren't currently being viewed.
        if (IsAllSelected)
        {
            return;
        }
        foreach (var entry in Entries)
        {
            if (entry.Subscription is { } sub &&
                sub.Topic == message.Topic &&
                sub.ServerUrl == message.ServerUrl &&
                entry != SelectedEntry)
            {
                entry.UnreadCount++;
            }
        }
    }

    private void RebuildEntries()
    {
        // The "All notifications" entry at index 0 is persistent and never removed.
        // Clearing the collection that backs NavigationView.MenuItemsSource — especially
        // while its selected item is in it — crashes the control, so we keep index 0 and
        // only reconcile the topic rows after it (removing from the end, never Clear()).
        if (Entries.Count == 0)
        {
            Entries.Add(new NavEntry(null)); // "All notifications"
        }
        while (Entries.Count > 1)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
        if (_manager.Subscriptions.Count > 0)
        {
            Entries.Add(NavEntry.CreateHeader("Subscribed topics"));
            foreach (var sub in _manager.Subscriptions)
            {
                Entries.Add(new NavEntry(sub));
            }
        }
    }

    private void RebuildCurrentMessages()
    {
        CurrentMessages.Clear();
        // manager.Messages is newest-first; show chronologically (oldest at top,
        // newest at the bottom) like a chat, matching the official app.
        var query = _manager.Messages.AsEnumerable();
        if (SelectedSubscription is { } sub)
        {
            query = query.Where(m => m.Topic == sub.Topic && ServerCredential.SameServer(m.ServerUrl, sub.ServerUrl));
        }
        foreach (var m in query.Reverse())
        {
            CurrentMessages.Add(m);
        }
        HasMessages = CurrentMessages.Count > 0;
    }

    // --- Quick compose bar (bottom of the topic feed) ---

    [ObservableProperty]
    public partial string ComposeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ComposeExpanded { get; set; }

    [ObservableProperty]
    public partial string ComposeTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ComposeTags { get; set; } = string.Empty;

    /// <summary>0..4 maps to priority 1..5 (2 = default).</summary>
    [ObservableProperty]
    public partial int ComposePriorityIndex { get; set; } = 2;

    [RelayCommand]
    private async Task SendCompose()
    {
        if (SelectedSubscription is not { } sub || string.IsNullOrWhiteSpace(ComposeText))
        {
            return;
        }

        var text = ComposeText;
        var title = string.IsNullOrWhiteSpace(ComposeTitle) ? null : ComposeTitle.Trim();
        var tags = string.IsNullOrWhiteSpace(ComposeTags) ? null : ComposeTags.Trim();
        ComposeText = string.Empty;

        await _manager.PublishQuickAsync(sub.ServerUrl, sub.Topic, text, title, ComposePriorityIndex + 1, tags);
    }

    /// <summary>Re-raises toolbar-dependent properties (e.g. after a mute toggle).</summary>
    public void RefreshToolbar()
    {
        OnPropertyChanged(nameof(SelectedIsMuted));
        OnPropertyChanged(nameof(CanActOnTopic));
    }

    /// <summary>Removes the selected topic's messages (or all, when "All" is selected).</summary>
    public void ClearCurrent()
    {
        if (SelectedSubscription is { } sub)
        {
            _manager.ClearMessagesForTopic(sub.ServerUrl, sub.Topic);
        }
        else
        {
            _manager.ClearHistory();
        }
    }
}
