using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NtfyDesktop.Models;
using NtfyDesktop.ViewModels;

namespace NtfyDesktop.Views;

/// <summary>
/// The settings pane shown in the content area (server default, notifications,
/// tray, theme, history, subscribed topics, and authentication).
/// </summary>
public sealed partial class SettingsView : UserControl
{
    public SettingsPageViewModel ViewModel { get; } = new();

    public SettingsView()
    {
        InitializeComponent();
    }

    private void Unsubscribe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Subscription sub })
        {
            ViewModel.UnsubscribeCommand.Execute(sub);
        }
    }

    private void RemoveCredential_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ServerCredential cred })
        {
            ViewModel.RemoveCredentialCommand.Execute(cred);
        }
    }

    private async void AddCredential_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BeginAddCredentialCommand.Execute(null);
        await new CredentialDialog(ViewModel) { XamlRoot = XamlRoot }.ShowAsync();
    }

    private async void EditCredential_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ServerCredential cred })
        {
            ViewModel.EditCredentialCommand.Execute(cred);
            await new CredentialDialog(ViewModel) { XamlRoot = XamlRoot }.ShowAsync();
        }
    }
}
