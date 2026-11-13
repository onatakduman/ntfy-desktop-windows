using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NtfyDesktop.Models;
using NtfyDesktop.Services;
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
        SetUpTrayIcon();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try
        {
            // Absolute path: a packaged app's working directory is System32, so the
            // old relative "Assets/AppIcon.ico" didn't resolve when started from Start.
            AppWindow.SetIcon(AppIcon.FilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Setting the window icon failed: {ex.Message}");
        }

        ApplyTheme(App.Settings.Theme);

        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(800 * scale)));

        // Enforce a minimum window size.
        _subclassProc = MinMaxSubclass;
        SetWindowSubclass(hwnd, _subclassProc, 1, IntPtr.Zero);

        AppWindow.Closing += AppWindow_Closing;

        // Clicking the topic that is already selected doesn't change the selection,
        // so it never left the Settings pane. Listen for taps (handled ones too, since
        // the list item handles them) and leave Settings explicitly.
        TopicList.AddHandler(UIElement.TappedEvent, new Microsoft.UI.Xaml.Input.TappedEventHandler(TopicList_Tapped), handledEventsToo: true);

        // Reflect the selected topic in the window/title-bar title.
        ViewModel.WindowTitleChanged += t =>
        {
            Title = t;
            AppTitleBar.Title = t;
        };
        // The sidebar ListView's SelectedItem is bound TwoWay to ViewModel.SelectedEntry,
        // so programmatic selection changes flow automatically — no manual sync needed.
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

    private void TopicList_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (!ViewModel.ShowSettings)
        {
            return;
        }

        for (var element = e.OriginalSource as DependencyObject;
             element is not null && element != TopicList;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
            {
                return; // the row's "More" menu button
            }
            if (element is ListViewItem { Content: NavEntry { IsHeader: false } })
            {
                ViewModel.ShowSettings = false;
                return;
            }
        }
    }

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
            // notifySelf: the whole point of a test is to see the toast, so it is not
            // filtered out like other messages published from this app.
            await App.Subscriptions.PublishQuickAsync(
                sub.ServerUrl, sub.Topic,
                "This is a test notification sent from Notidesk.",
                title: "Test", priority: 3, tags: "white_check_mark", notifySelf: true);
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
        if (_forceClose)
        {
            return;
        }

        args.Cancel = true;

        if (App.Settings.MinimizeToTrayOnClose)
        {
            // Hide to tray instead of exiting; background streaming keeps running.
            AppWindow.Hide();
            return;
        }

        // Tray mode off: closing the window quits the app. Just letting the window
        // close left the process running (the tray menu's hidden helper window keeps
        // it alive) with a tray icon whose "Open" no longer worked.
        App.ExitApplication();
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

    /// <summary>
    /// Sets the tray icon's image and click command in code and creates it right
    /// away. The XAML <c>IconSource</c> left the icon blank (see <see cref="AppIcon"/>),
    /// and the default creation on <c>Loaded</c> never happens when the app starts
    /// hidden in the tray, because the window content isn't loaded until first shown.
    /// </summary>
    private void SetUpTrayIcon()
    {
        TrayIcon.LeftClickCommand = ShowCommand;

        if (AppIcon.LoadTrayIcon() is { } icon)
        {
            TrayIcon.Icon = icon;
        }

        try
        {
            TrayIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            // Not fatal: the control retries when the window content loads.
            Debug.WriteLine($"Creating the tray icon failed: {ex}");
        }
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
