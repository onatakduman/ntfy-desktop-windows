using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using NtfyDesktop.Models;

namespace NtfyDesktop.Services;

/// <summary>
/// Raises native Windows toast notifications (Windows App SDK
/// <see cref="AppNotificationManager"/>) for incoming ntfy messages, applies the
/// configured toast sound, renders icon/image/action buttons, and routes
/// notification activation (clicking a toast or its action buttons).
/// </summary>
public sealed class NotificationService
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_NODEFAULT = 0x0002;

    private bool _registered;

    /// <summary>
    /// The selectable toast sounds: key -&gt; (display label, ms-winsoundevent,
    /// looping, preview .wav filename under %WINDIR%\Media). Order matters for
    /// the Settings dropdown.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label, string? SoundEvent, bool Loop, string? Wav)> Sounds =
        new List<(string, string, string?, bool, string?)>
        {
            ("Default", "Default", "ms-winsoundevent:Notification.Default", false, "Windows Notify System Generic.wav"),
            ("IM", "Instant message", "ms-winsoundevent:Notification.IM", false, "Windows Notify Messaging.wav"),
            ("Mail", "Mail", "ms-winsoundevent:Notification.Mail", false, "Windows Notify Email.wav"),
            ("Reminder", "Reminder", "ms-winsoundevent:Notification.Reminder", false, "Windows Notify Calendar.wav"),
            ("SMS", "SMS", "ms-winsoundevent:Notification.SMS", false, "Windows Notify Messaging.wav"),
            ("Alarm", "Alarm (looping)", "ms-winsoundevent:Notification.Looping.Alarm", true, "Alarm01.wav"),
            ("Call", "Call (looping)", "ms-winsoundevent:Notification.Looping.Call", true, "Ring01.wav"),
            ("Silent", "Silent", null, false, null),
        };

    /// <summary>
    /// Registers the notification manager and wires activation handling. Call
    /// once during app startup before any toast is shown.
    /// </summary>
    public void Register()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Don't let a notification-platform hiccup crash startup; toasts will
            // simply be unavailable for this session.
            Debug.WriteLine($"AppNotification registration failed: {ex.Message}");
        }
    }

    /// <summary>True if the notification manager registered successfully.</summary>
    public bool IsRegistered => _registered;

    public void Unregister()
    {
        if (_registered)
        {
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }

    /// <summary>
    /// Builds and shows a toast for the given message, applying the configured
    /// sound, priority emphasis, icon/image, and "view" action buttons.
    /// </summary>
    public void Show(NtfyMessage message)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var title = message.GetEmojiPrefix() + message.DisplayTitle;

            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(string.IsNullOrWhiteSpace(message.DisplayBody)
                    ? message.Topic
                    : message.DisplayBody);

            if (message.HasTags)
            {
                builder.AddText(message.TagsDisplay);
            }

            if (message.Priority >= 4)
            {
                builder.SetScenario(AppNotificationScenario.Urgent);
            }

            // Icon as the app-logo override; image attachments as an inline image.
            if (message.IconUri is not null)
            {
                builder.SetAppLogoOverride(message.IconUri, AppNotificationImageCrop.Circle);
            }
            if (message.HasImageAttachment && message.AttachmentUri is not null)
            {
                builder.SetInlineImage(message.AttachmentUri);
            }

            // "view" action buttons open their URL directly.
            if (message.HasActions)
            {
                var count = 0;
                foreach (var action in message.Actions!)
                {
                    if (count >= 3)
                    {
                        break;
                    }
                    if (string.Equals(action.Action, "view", StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreate(action.Url, UriKind.Absolute, out var aUri))
                    {
                        builder.AddButton(new AppNotificationButton(action.Label ?? "Open").SetInvokeUri(aUri));
                        count++;
                    }
                }
            }

            ApplyAudio(builder, App.Settings.NotificationSound);

            if (message.HasClick)
            {
                builder.AddArgument("click", message.Click);
            }
            builder.AddArgument("topic", message.Topic);

            var notification = builder.BuildNotification();

            if (message.Priority <= 2)
            {
                notification.Expiration = DateTimeOffset.Now.AddHours(8);
            }

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Show notification failed: {ex.Message}");
        }
    }

    private static void ApplyAudio(AppNotificationBuilder builder, string? soundKey)
    {
        var entry = Sounds.FirstOrDefaultByKey(soundKey);
        if (entry.SoundEvent is null)
        {
            builder.MuteAudio();
            return;
        }
        try
        {
            builder.SetAudioUri(
                new Uri(entry.SoundEvent),
                entry.Loop ? AppNotificationAudioLooping.Loop : AppNotificationAudioLooping.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SetAudio failed: {ex.Message}");
        }
    }

    /// <summary>Plays the preview .wav for a sound key (used by the Settings ▶ button).</summary>
    public void PreviewSound(string? soundKey)
    {
        var entry = Sounds.FirstOrDefaultByKey(soundKey);
        if (string.IsNullOrEmpty(entry.Wav))
        {
            return;
        }
        try
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var path = Path.Combine(windir, "Media", entry.Wav);
            if (File.Exists(path))
            {
                PlaySound(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PreviewSound failed: {ex.Message}");
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Bring the app to the foreground on any toast body click.
        App.DispatcherQueue?.TryEnqueue(() => App.ShowMainWindow());

        if (args.Arguments.TryGetValue("click", out var url) && !string.IsNullOrWhiteSpace(url))
        {
            TryOpenUrl(url);
        }
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.ToString(),
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open URL failed: {ex.Message}");
        }
    }
}

internal static class SoundExtensions
{
    public static (string Key, string Label, string? SoundEvent, bool Loop, string? Wav) FirstOrDefaultByKey(
        this IReadOnlyList<(string Key, string Label, string? SoundEvent, bool Loop, string? Wav)> sounds, string? key)
    {
        foreach (var s in sounds)
        {
            if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return sounds[0]; // Default
    }
}
