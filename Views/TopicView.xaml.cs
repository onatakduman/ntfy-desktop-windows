using System;
using System.Diagnostics;
using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NtfyDesktop.Models;
using NtfyDesktop.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace NtfyDesktop.Views;

/// <summary>
/// The main pane: top app bar of topic actions, the chronological notification
/// feed for the selected topic (rich cards), and a quick-publish compose bar at
/// the bottom. Mirrors the ntfy web app's content area.
/// </summary>
public sealed partial class TopicView : UserControl
{
    public MainViewModel ViewModel => App.Main;

    public TopicView()
    {
        InitializeComponent();
    }

    // --- Top app bar actions ---

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedSubscription is { } sub)
        {
            await App.Subscriptions.SetMutedAsync(sub, !sub.IsMuted);
            ViewModel.RefreshToolbar();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.ClearCurrent();

    private async void SubscribeEmpty_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SubscribeDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private void CopyExample_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ViewModel.ExampleCommand);

    // --- Message card interactions ---

    /// <summary>Renders a Markdown message body into the TextBlock once it's realized.</summary>
    private void MarkdownBody_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb && tb.DataContext is NtfyMessage msg)
        {
            MarkdownRenderer.Render(tb, msg.DisplayBody);
        }
    }

    private void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NtfyMessage { AttachmentUri: { } uri } })
        {
            OpenUrl(uri.ToString());
        }
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NtfyAction action })
        {
            return;
        }

        switch (action.Action?.ToLowerInvariant())
        {
            case "view":
                if (!string.IsNullOrWhiteSpace(action.Url))
                {
                    OpenUrl(action.Url);
                }
                break;
            case "http":
                await ExecuteHttpActionAsync(action);
                break;
            default:
                // "broadcast" is Android-only; nothing meaningful to do on desktop.
                break;
        }
    }

    private static async System.Threading.Tasks.Task ExecuteHttpActionAsync(NtfyAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Url) ||
            !Uri.TryCreate(action.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            var method = string.IsNullOrWhiteSpace(action.Method) ? "POST" : action.Method!.ToUpperInvariant();
            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            if (!string.IsNullOrEmpty(action.Body))
            {
                request.Content = new StringContent(action.Body);
            }
            await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"HTTP action failed: {ex.Message}");
        }
    }

    private void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NtfyMessage msg })
        {
            App.Subscriptions.DeleteMessage(msg);
        }
    }

    // --- Compose bar ---

    private void ComposeBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter sends; Shift+Enter is not handled (single-line box).
        if (e.Key == VirtualKey.Enter)
        {
            var shift = (Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift) &
                Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (!shift && ViewModel.SendComposeCommand.CanExecute(null))
            {
                e.Handled = true;
                ViewModel.SendComposeCommand.Execute(null);
            }
        }
    }

    // --- Helpers ---

    private static void CopyToClipboard(string text)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard copy failed: {ex.Message}");
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open URL failed: {ex.Message}");
        }
    }
}
