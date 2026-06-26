namespace NtfyDesktop.Models;

/// <summary>
/// How a subscription or publish request authenticates against the ntfy server.
/// </summary>
public enum AuthMode
{
    /// <summary>No authentication header is sent.</summary>
    None = 0,

    /// <summary>Bearer token: <c>Authorization: Bearer tk_...</c>.</summary>
    Token = 1,

    /// <summary>Basic auth: <c>Authorization: Basic base64(user:pass)</c>.</summary>
    UsernamePassword = 2,
}
