using System;
using System.Diagnostics;

namespace NtfyDesktop.Services;

/// <summary>
/// Opens URLs that come from message content (click URLs, attachments, action
/// buttons, markdown links) in the user's default handler.
/// </summary>
/// <remarks>
/// Anyone who can publish to a topic controls these values; on public servers
/// that is anyone who knows the topic name. Only web and mail links are opened:
/// handing arbitrary URIs to the shell would let a single click on a notification
/// run a local or network executable (<c>file:</c>, UNC paths) or an arbitrary
/// protocol handler.
/// </remarks>
public static class UrlLauncher
{
    public static bool IsSafe(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    /// <summary>True for absolute http(s) URLs (images, HTTP actions).</summary>
    public static bool IsWeb(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Parses <paramref name="url"/> and returns it only if it is safe to open.</summary>
    public static Uri? ToSafeUri(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && IsSafe(uri) ? uri : null;

    /// <summary>Opens a URL from message content; anything but http/https/mailto is ignored.</summary>
    public static void Open(string? url)
    {
        var uri = ToSafeUri(url);
        if (uri is null)
        {
            Debug.WriteLine($"Refusing to open non-web URL from a message: {url}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open URL failed: {ex.Message}");
        }
    }
}
