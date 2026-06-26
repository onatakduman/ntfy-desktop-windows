using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace NtfyDesktop.Views;

/// <summary>
/// Static helpers for <c>x:Bind</c> function bindings, avoiding IValueConverter
/// per the project's XAML conventions.
/// </summary>
public static class XamlHelpers
{
    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility StringToVisibility(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static bool Negate(bool value) => !value;

    public static bool StringToBool(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Visibility CountToVisibility(int count) =>
        count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CountToInvertVisibility(int count) =>
        count > 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Visible only when there are no subscriptions at all.</summary>
    public static Visibility NoSubsVisibility(bool hasSubscriptions) =>
        hasSubscriptions ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Visible when subscriptions exist but the selected topic has no messages.</summary>
    public static Visibility EmptyTopicVisibility(bool hasSubscriptions, bool hasMessages) =>
        hasSubscriptions && !hasMessages ? Visibility.Visible : Visibility.Collapsed;

    //  = Mute glyph,  = Volume glyph (Segoe Fluent Icons).
    public static string MuteGlyph(bool isMuted) => isMuted ? "" : "";

    public static string MuteTooltip(bool isMuted) =>
        isMuted ? "Unmute (resume streaming)" : "Mute (pause streaming)";

    public static InfoBarSeverity ErrorToSeverity(bool isError) =>
        isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    /// <summary>Builds a BitmapImage from a URI for binding to Image.Source (null-safe).</summary>
    public static BitmapImage? ImageFromUri(Uri? uri) => uri is null ? null : new BitmapImage(uri);
}
