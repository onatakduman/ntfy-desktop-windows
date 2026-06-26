using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using NtfyDesktop.Models;
using Windows.UI;

namespace NtfyDesktop.ViewModels;

/// <summary>
/// A single entry in the sidebar topic list. Either the special "All
/// notifications" entry (<see cref="IsAll"/>) or a wrapper around a
/// <see cref="Subscription"/>, exposing the bits the sidebar template needs and
/// forwarding the subscription's change notifications.
/// </summary>
public enum NavEntryKind
{
    All,
    Header,
    Topic,
}

public partial class NavEntry : ObservableObject
{
    public NavEntry(Subscription? subscription)
    {
        Subscription = subscription;
        Kind = subscription is null ? NavEntryKind.All : NavEntryKind.Topic;
        if (subscription is not null)
        {
            subscription.PropertyChanged += OnSubscriptionPropertyChanged;
        }
    }

    private NavEntry(string headerText)
    {
        Kind = NavEntryKind.Header;
        _headerText = headerText;
    }

    /// <summary>A non-selectable section header row (e.g. "Subscribed topics").</summary>
    public static NavEntry CreateHeader(string text) => new(text);

    private readonly string? _headerText;

    public NavEntryKind Kind { get; }

    /// <summary>The wrapped subscription, or null for the "All notifications"/header entries.</summary>
    public Subscription? Subscription { get; }

    public bool IsAll => Kind == NavEntryKind.All;

    public bool IsHeader => Kind == NavEntryKind.Header;

    public bool IsTopic => Kind == NavEntryKind.Topic;

    public string HeaderText => _headerText ?? string.Empty;

    [ObservableProperty]
    public partial int UnreadCount { get; set; }

    public bool HasUnread => UnreadCount > 0;

    public string Title => Subscription?.DisplayLabel ?? "All notifications";

    public string Subtitle
    {
        get
        {
            if (Subscription is null)
            {
                return "Every subscribed topic";
            }
            if (Uri.TryCreate(Subscription.ServerUrl, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }
            return Subscription.ServerUrl;
        }
    }

    /// <summary>Uppercase first letter of the topic, for the avatar circle.</summary>
    public string AvatarLetter =>
        string.IsNullOrEmpty(Subscription?.Topic) ? "#" : Subscription!.Topic.Substring(0, 1).ToUpperInvariant();

    /// <summary>A stable colour derived from the topic name (ntfy-style coloured avatars).</summary>
    public Brush AvatarBrush
    {
        get
        {
            if (Subscription is null)
            {
                return new SolidColorBrush(ColorFromHex("#33A474"));
            }
            // Deterministic hue from the topic string.
            var hash = 0;
            foreach (var c in Subscription.Topic)
            {
                hash = c + ((hash << 5) - hash);
            }
            var palette = new[]
            {
                "#E5634D", "#E59A3F", "#4F9D69", "#3F7FE5", "#8A5CD1",
                "#D14F9D", "#3FB6C2", "#C2A33F", "#6C7BD1", "#4FB07A",
            };
            var color = palette[Math.Abs(hash) % palette.Length];
            return new SolidColorBrush(ColorFromHex(color));
        }
    }

    public bool IsMuted => Subscription?.IsMuted ?? false;

    /// <summary>Context-menu label that flips with mute state.</summary>
    public string MuteLabel => IsMuted ? "Unmute notifications" : "Mute notifications";

    public ConnectionStatus Status => Subscription?.Status ?? ConnectionStatus.Connected;

    public bool ShowStatusDot => Subscription is not null;

    public Brush StatusBrush => Status switch
    {
        ConnectionStatus.Connected => new SolidColorBrush(ColorFromHex("#33A474")),
        ConnectionStatus.Connecting => new SolidColorBrush(ColorFromHex("#E59A3F")),
        ConnectionStatus.Error => new SolidColorBrush(ColorFromHex("#E5634D")),
        _ => new SolidColorBrush(Colors.Gray),
    };

    private void OnSubscriptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Subscription.IsMuted):
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(MuteLabel));
                break;
            case nameof(Subscription.Status):
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusBrush));
                break;
            case nameof(Subscription.Topic):
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(AvatarLetter));
                OnPropertyChanged(nameof(AvatarBrush));
                break;
            case nameof(Subscription.Label):
            case nameof(Subscription.DisplayLabel):
                OnPropertyChanged(nameof(Title));
                break;
            case nameof(Subscription.ServerUrl):
                OnPropertyChanged(nameof(Subtitle));
                break;
        }
    }

    partial void OnUnreadCountChanged(int value) => OnPropertyChanged(nameof(HasUnread));

    private static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        var r = Convert.ToByte(hex.Substring(0, 2), 16);
        var g = Convert.ToByte(hex.Substring(2, 2), 16);
        var b = Convert.ToByte(hex.Substring(4, 2), 16);
        return Color.FromArgb(255, r, g, b);
    }
}
