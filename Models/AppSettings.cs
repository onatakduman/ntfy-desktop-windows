using System.Collections.Generic;

namespace NtfyDesktop.Models;

/// <summary>
/// Application-wide settings persisted to disk.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Saved server credentials, used as fallback auth (Settings &gt; Authentication).</summary>
    public List<ServerCredential> Credentials { get; set; } = new();

    /// <summary>Default server pre-filled when adding a subscription or publishing.</summary>
    public string DefaultServerUrl { get; set; } = "https://ntfy.sh";

    /// <summary>Raise Windows toast notifications for incoming messages.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Hide to the system tray instead of exiting when the window is closed.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Maximum number of messages retained in local history.</summary>
    public int MaxHistory { get; set; } = 1000;

    /// <summary>App theme: "Default", "Light", or "Dark".</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>
    /// Toast sound key (see <c>NotificationService.Sounds</c>): "Default", "IM",
    /// "Mail", "Reminder", "SMS", "Alarm", "Call", or "Silent".
    /// </summary>
    public string NotificationSound { get; set; } = "Default";

    /// <summary>Only raise a toast for messages with priority &gt;= this value (1-5).</summary>
    public int MinNotificationPriority { get; set; } = 1;
}
