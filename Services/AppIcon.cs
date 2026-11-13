using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NtfyDesktop.Services;

/// <summary>
/// Loads the app icon for the system tray and the window.
/// </summary>
/// <remarks>
/// The tray icon used to come from <c>IconSource="Assets/AppIcon.ico"</c> in XAML,
/// which H.NotifyIcon resolves asynchronously (ms-appx URI → package file → icon).
/// Any failure on that path is thrown from an <c>async void</c> handler and was
/// swallowed by <c>App.UnhandledException</c>, leaving an icon with a working tooltip
/// and menu but no image. The icon is now embedded in the assembly and converted
/// synchronously at the system's small-icon size, so the tray image no longer
/// depends on the package layout, the working directory or URI resolution.
/// </remarks>
internal static class AppIcon
{
    private const string ResourceName = "NtfyDesktop.AppIcon.ico";
    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Absolute path of the loose .ico shipped with the app. Packaged apps start with
    /// System32 as their working directory, so relative paths must never be used.
    /// </summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    /// <summary>
    /// Returns the tray icon at the system's small-icon size, or null if it could not
    /// be loaded (the caller keeps whatever icon is already set).
    /// </summary>
    public static System.Drawing.Icon? LoadTrayIcon()
    {
        var size = SmallIconSize();

        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                return new System.Drawing.Icon(stream, size, size);
            }
            Debug.WriteLine($"Tray icon: embedded resource '{ResourceName}' not found.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tray icon: loading embedded icon failed: {ex}");
        }

        try
        {
            if (File.Exists(FilePath))
            {
                return new System.Drawing.Icon(FilePath, size, size);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tray icon: loading {FilePath} failed: {ex}");
        }

        return null;
    }

    private static int SmallIconSize()
    {
        try
        {
            var size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
            if (size > 0)
            {
                return size;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 before 1607 has no per-DPI metrics; fall through.
        }

        var fallback = GetSystemMetrics(SM_CXSMICON);
        return fallback > 0 ? fallback : 16;
    }
}
