using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NtfyDesktop.Models;
using NtfyDesktop.ViewModels;
using NtfyDesktop.Views;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace NtfyDesktop;

/// <summary>
/// The application window. Mirrors the ntfy web app: a sidebar listing
/// "All notifications" plus each subscribed topic, and a content pane showing
/// the selected topic's notifications. A "Subscribe to topic" button sits at the
/// top of the pane and Settings at the bottom; a system-tray icon keeps the app
/// streaming in the background when the window is closed.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // --- Minimum window size (WinUI 3 has no built-in min size; hook WM_GETMINMAXINFO) ---
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const int MinLogicalWidth = 720;
    private const int MinLogicalHeight = 560;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    // Held as a field so the delegate isn't garbage-collected while subclassed.
    private SubclassProc? _subclassProc;

    private bool _forceClose;

    public MainViewModel ViewModel => App.Main;

    /// <summary>Command backing the tray icon's left-click (show the window).</summary>
    public ICommand ShowCommand { get; }

    public MainWindow()
    {
        InitializeComponent();

        ShowCommand = new RelayCommand(ShowAndActivate);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        ApplyTheme(App.Settings.Theme);

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(800 * scale)));

        // Enforce a minimum window size.
        _subclassProc = MinMaxSubclass;
        SetWindowSubclass(hwnd, _subclassProc, 1, IntPtr.Zero);

        AppWindow.Closing += AppWindow_Closing;

        // Reflect the selected topic in the window/title-bar title.
        ViewModel.WindowTitleChanged += t =>
        {
            Title = t;
            AppTitleBar.Title = t;
        };
        // NavigationView.SelectedItem is bound TwoWay to ViewModel.SelectedEntry, so
        // programmatic selection changes flow automatically — no manual sync needed.
    }

    /// <summary>Applies the persisted theme to the window's root element.</summary>
    public void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>Clamps the window's minimum track size (DPI-aware) via WM_GETMINMAXINFO.</summary>
    private IntPtr MinMaxSubclass(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO)
        {
            var dpi = GetDpiForWindow(hWnd) / 96.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = (int)(MinLogicalWidth * dpi);
            mmi.ptMinTrackSize.Y = (int)(MinLogicalHeight * dpi);
            Marshal.StructureToPtr(mmi, lParam, false);
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private async void Subscribe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SubscribeDialog { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        var sub = ViewModel.SelectedSubscription;
        var dialog = new SendDialog(sub?.ServerUrl ?? App.Settings.DefaultServerUrl, sub?.Topic)
        {
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowSettings = true;
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        // Clicking any topic returns from the Settings pane to the feed.
        if (args.InvokedItemContainer?.DataContext is not NavEntry { IsHeader: true })
        {
            ViewModel.ShowSettings = false;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // The "Subscribed topics" header is not selectable — revert to the last real
        // entry (the TwoWay SelectedItem binding may have already set it momentarily).
        if (args.SelectedItem is NavEntry { IsHeader: true })
        {
            // Setting SelectedItem flows back to the view model via the TwoWay binding.
            sender.SelectedItem = _lastSelectableEntry ?? ViewModel.Entries.FirstOrDefault(e => !e.IsHeader);
            return;
        }

        // Remember the last selectable (non-header) entry for header reverts.
        if (args.SelectedItem is NavEntry entry)
        {
            _lastSelectableEntry = entry;
        }
    }

    private NavEntry? _lastSelectableEntry;

    // --- Per-topic sidebar menu ---

    private static Subscription? SubFrom(object sender)
    {
        var dc = (sender as FrameworkElement)?.DataContext;
        return dc as Subscription ?? (dc as NavEntry)?.Subscription;
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SubFrom(sender) is not { } sub)
        {
            return;
        }

        var input = new TextBox
        {
            Text = sub.Label ?? sub.Topic,
            PlaceholderText = sub.Topic,
            SelectionStart = 0,
        };
        var dialog = new ContentDialog
        {
            Title = "Change display name",
            Content = input,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var name = input.Text.Trim();
            sub.Label = string.IsNullOrWhiteSpace(name) || name == sub.Topic ? null : name;
            await App.Subscriptions.SaveSubscriptionsAsync();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            sub.Label = null;
            await App.Subscriptions.SaveSubscriptionsAsync();
        }
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        if (SubFrom(sender) is { } sub)
        {
            await App.Subscriptions.PublishQuickAsync(
                sub.ServerUrl, sub.Topic,
                "This is a test notification sent from ntfy Desktop.",
                title: "Test", priority: 3, tags: "white_check_mark");
        }
    }

    private void ClearTopic_Click(object sender, RoutedEventArgs e)
    {
        if (SubFrom(sender) is { } sub)
        {
            App.Subscriptions.ClearMessagesForTopic(sub.ServerUrl, sub.Topic);
        }
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (SubFrom(sender) is { } sub)
        {
            await App.Subscriptions.SetMutedAsync(sub, !sub.IsMuted);
        }
    }

    private async void Unsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (SubFrom(sender) is not { } sub)
        {
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Unsubscribe",
            Content = $"Stop streaming and remove '{sub.DisplayLabel}'? Its message history will also be removed.",
            PrimaryButtonText = "Unsubscribe",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            App.Subscriptions.ClearMessagesForTopic(sub.ServerUrl, sub.Topic);
            await App.Subscriptions.RemoveSubscriptionAsync(sub);
        }
    }

    // --- Tray / window visibility ---

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || !App.Settings.MinimizeToTrayOnClose)
        {
            return;
        }

        // Hide to tray instead of exiting; background streaming keeps running.
        args.Cancel = true;
        AppWindow.Hide();
    }

    /// <summary>Shows the window and brings it to the foreground.</summary>
    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
        }
        Activate();

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Allows the next close to actually close the window (used by tray "Exit").</summary>
    public void ForceClose()
    {
        _forceClose = true;
        TrayIcon?.Dispose();
        Close();
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowAndActivate();

    private void TrayExit_Click(object sender, RoutedEventArgs e) => App.ExitApplication();
}
