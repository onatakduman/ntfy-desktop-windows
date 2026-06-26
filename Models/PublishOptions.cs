namespace NtfyDesktop.Models;

/// <summary>
/// All the options for a single publish request. Maps to the ntfy publish
/// headers (see https://docs.ntfy.sh/publish/). Auth is optional here; the
/// manager falls back to a saved credential matching the server.
/// </summary>
public sealed class PublishOptions
{
    public string ServerUrl { get; set; } = "https://ntfy.sh";

    public string Topic { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? Title { get; set; }

    /// <summary>1-5 (3 = default).</summary>
    public int Priority { get; set; } = 3;

    /// <summary>Comma-separated tags/emoji shortcodes (X-Tags).</summary>
    public string? Tags { get; set; }

    /// <summary>Render the body as Markdown (X-Markdown / Content-Type text/markdown).</summary>
    public bool Markdown { get; set; }

    /// <summary>Click URL opened when the notification is tapped (X-Click).</summary>
    public string? Click { get; set; }

    /// <summary>URL of a file to attach by reference (X-Attach).</summary>
    public string? AttachUrl { get; set; }

    /// <summary>Override/derived filename (X-Filename).</summary>
    public string? Filename { get; set; }

    /// <summary>Local file to upload via PUT (the file becomes the body).</summary>
    public string? LocalFilePath { get; set; }

    /// <summary>Forward a copy to this email address (X-Email).</summary>
    public string? Email { get; set; }

    /// <summary>Delayed delivery: a duration ("30min"), timestamp, or natural language (X-Delay).</summary>
    public string? Delay { get; set; }

    /// <summary>Icon URL (X-Icon).</summary>
    public string? Icon { get; set; }

    /// <summary>Raw X-Actions header value (e.g. "view, Open, https://...").</summary>
    public string? Actions { get; set; }

    // Optional explicit auth; otherwise resolved from saved credentials by server.
    public AuthMode AuthMode { get; set; } = AuthMode.None;

    public string? Token { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }
}
