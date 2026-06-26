using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NtfyDesktop.ViewModels;
using Windows.Storage.Pickers;

namespace NtfyDesktop.Views;

/// <summary>"Publish notification" dialog with full ntfy feature coverage.</summary>
public sealed partial class SendDialog : ContentDialog
{
    public SendDialogViewModel ViewModel { get; }

    public SendDialog(string? server, string? topic)
    {
        ViewModel = new SendDialogViewModel(server, topic);
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        var ok = await ViewModel.PublishAsync();
        if (!ok)
        {
            args.Cancel = true;
        }
        deferral.Complete();
    }

    private async void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        // Pickers need the owning window's HWND in WinUI 3.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.LocalFilePath = file.Path;
        }
    }

    private void ClearFile_Click(object sender, RoutedEventArgs e) => ViewModel.ClearLocalFile();
}
