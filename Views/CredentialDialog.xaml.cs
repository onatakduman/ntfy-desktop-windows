using Microsoft.UI.Xaml.Controls;
using NtfyDesktop.ViewModels;

namespace NtfyDesktop.Views;

/// <summary>
/// Add/edit dialog for a saved server credential (Settings &gt; Authentication).
/// Validates on the primary click and commits in the Closed handler — mutating the
/// bound credential list while the dialog is dismissing crashes the WARP compositor.
/// </summary>
public sealed partial class CredentialDialog : ContentDialog
{
    public SettingsPageViewModel ViewModel { get; }

    private bool _commit;

    public CredentialDialog(SettingsPageViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Closed += OnClosed;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ViewModel.ValidateCredential())
        {
            _commit = true;
        }
        else
        {
            args.Cancel = true;
        }
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_commit)
        {
            _commit = false;
            ViewModel.CommitCredential();
        }
    }
}
