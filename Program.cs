using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace NtfyDesktop;

/// <summary>
/// Custom entry point (the XAML-generated Main is disabled with
/// DISABLE_XAML_GENERATED_MAIN) that makes Notidesk single-instance.
/// </summary>
/// <remarks>
/// WinUI apps are multi-instance by default, and Notidesk keeps running in the tray
/// after its window is closed. Opening it again from Start (or being started at
/// sign-in while already running) used to create a second process: a second tray
/// icon, every subscription streamed twice, a duplicate toast for every message, and
/// two processes writing the same history files. Now any later launch hands its
/// activation to the running instance, which shows its window, and exits.
/// </remarks>
public static class Program
{
    private const string InstanceKey = "Notidesk.Main";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    private const uint CWMO_DEFAULT = 0;
    private const uint INFINITE = 0xFFFFFFFF;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToRunningInstance())
        {
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <summary>
    /// Registers this process as the main instance, or, if another instance already
    /// is, redirects this activation to it. Returns true if this process should exit.
    /// </summary>
    private static bool RedirectToRunningInstance()
    {
        AppActivationArguments activation;
        AppInstance mainInstance;
        try
        {
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch (Exception ex)
        {
            // Better to run as a second instance than not at all.
            Debug.WriteLine($"Single-instance check failed: {ex}");
            return false;
        }

        if (mainInstance.IsCurrent)
        {
            // Raised on a background thread when a later launch redirects to us.
            mainInstance.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        // Let the running instance bring its window to the foreground (Windows only
        // allows that for the process the user just interacted with, i.e. this one).
        AllowSetForegroundWindow(mainInstance.ProcessId);

        // The redirect must not block this STA thread outright: run it on a worker
        // and pump COM messages while waiting for it to finish.
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            try
            {
                mainInstance.RedirectActivationToAsync(activation).AsTask().Wait();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Redirecting activation failed: {ex}");
            }
            finally
            {
                SetEvent(done);
            }
        });
        _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, new[] { done }, out _);
        CloseHandle(done);
        return true;
    }
}
