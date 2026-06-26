using Microsoft.UI.Xaml.Controls;
using NtfyDesktop.Models;
using NtfyDesktop.ViewModels;

namespace NtfyDesktop.Views;

/// <summary>"Subscribe to topic" / edit-subscription dialog.</summary>
public sealed partial class SubscribeDialog : ContentDialog
{
    public SubscribeDialogViewModel ViewModel { get; }

    private bool _commit;

    public SubscribeDialog(Subscription? editing = null)
    {
        ViewModel = new SubscribeDialogViewModel(editing);
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Closed += OnClosed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Only validate here (synchronous). Persisting the subscription updates the
        // sidebar NavigationView; doing that while the dialog is still dismissing
        // crashes the compositor on GPU-less machines, so the actual commit is
        // deferred to Closed, when the dialog is gone from the visual tree.
        if (ViewModel.Validate())
        {
            _commit = true;
        }
        else
        {
            args.Cancel = true;
        }
    }

    private async void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_commit)
        {
            _commit = false;
            await ViewModel.CommitAsync();
        }
    }
}
