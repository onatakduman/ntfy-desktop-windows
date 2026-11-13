using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using NtfyDesktop.Models;
using NtfyDesktop.Services;
using NtfyDesktop.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace NtfyDesktop;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    // --- Application services (simple singletons; the app is small enough that
    //     a full DI container would be overkill). ---

    public static PersistenceService Persistence { get; private set; } = null!;

    public static SubscriptionManager Subscriptions { get; private set; } = null!;

    public static NotificationService Notifications { get; private set; } = null!;

    public static AppSettings Settings { get; private set; } = null!;

    /// <summary>The shared shell view model (sidebar topics + current message pane).</summary>
    public static MainViewModel Main { get; private set; } = null!;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Last-resort guard: keep a stray exception on the UI thread (e.g. from an
        // async event handler or a transient network/IO failure) from tearing down
        // the whole app. Native faults (e.g. the GPU-less software rasterizer) can't
        // be caught here, but managed exceptions can.
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
            e.Handled = true;
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        // How we were started: a normal launch, Windows sign-in (startup task) or a
        // click on one of our toasts while the app wasn't running.
        AppActivationArguments? activation = null;
        try
        {
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetActivatedEventArgs failed: {ex.Message}");
        }

        Persistence = new PersistenceService();
        Settings = Persistence.LoadSettings();

        Notifications = new NotificationService();
        Notifications.Register();

        Subscriptions = new SubscriptionManager(Persistence, DispatcherQueue);
        Subscriptions.MessageReceived += OnMessageReceived;
        Subscriptions.Initialize(Settings.MaxHistory);

        Main = new MainViewModel();

        Window = new MainWindow();

        if (activation?.Kind == ExtendedActivationKind.StartupTask)
        {
            // Started at sign-in ("Start with Windows"): stay in the tray. The tray
            // icon exists already (MainWindow creates it up front) and streaming runs;
            // the window appears on a tray click, a toast click or "Open".
        }
        else
        {
            Window.Activate();
        }

        // A toast click that launched the app isn't raised as NotificationInvoked;
        // handle it here so its click URL still opens.
        if (activation?.Kind == ExtendedActivationKind.AppNotification &&
            activation.Data is AppNotificationActivatedEventArgs toastArgs)
        {
            Notifications.HandleActivation(toastArgs);
        }
    }

    /// <summary>
    /// Called (on a background thread) when another launch of the app was redirected
    /// to this instance, e.g. the user opened Notidesk from Start while it was
    /// running in the tray: show the existing window.
    /// </summary>
    public static void OnRedirectedActivation(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.StartupTask)
        {
            return; // already running; nothing to show at sign-in
        }
        DispatcherQueue?.TryEnqueue(ShowMainWindow);
    }

    private static void OnMessageReceived(NtfyMessage message)
    {
        // Messages published from this app are kept in history but not announced.
        if (message.IsOwn || !Settings.NotificationsEnabled)
        {
            return;
        }

        // Minimum-priority filter.
        if (message.Priority < Settings.MinNotificationPriority)
        {
            return;
        }

        // Per-topic mute: history is still recorded by the manager, but no toast.
        foreach (var sub in Subscriptions.Subscriptions)
        {
            if (sub.Topic == message.Topic &&
                ServerCredential.SameServer(sub.ServerUrl, message.ServerUrl) &&
                sub.IsMuted)
            {
                return;
            }
        }

        Notifications.Show(message);
    }

    /// <summary>Brings the main window to the foreground (from a tray click or toast).</summary>
    public static void ShowMainWindow()
    {
        (Window as MainWindow)?.ShowAndActivate();
    }

    /// <summary>Persists state and exits the process for real (tray "Exit").</summary>
    public static async void ExitApplication()
    {
        try
        {
            Subscriptions.StopAll();
            await Subscriptions.FlushMessagesAsync();
            await Persistence.SaveSettingsAsync(Settings);
            Notifications.Unregister();
        }
        finally
        {
            (Window as MainWindow)?.ForceClose();
            Current.Exit();
        }
    }
}
