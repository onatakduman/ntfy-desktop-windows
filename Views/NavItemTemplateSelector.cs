using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NtfyDesktop.ViewModels;

namespace NtfyDesktop.Views;

/// <summary>
/// Chooses the sidebar row template: a non-interactive section header for
/// <see cref="NavEntryKind.Header"/> entries, otherwise the avatar/topic row.
/// </summary>
public sealed partial class NavItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ItemTemplate { get; set; }

    public DataTemplate? HeaderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is NavEntry { IsHeader: true } ? HeaderTemplate : ItemTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
