using System;
using System.Text;
using System.Text.Json.Serialization;

namespace NtfyDesktop.Models;

/// <summary>
/// Saved authentication for a server, managed under Settings. Acts as a fallback
/// for any subscription or publish to that server that doesn't carry its own
/// auth (mirrors ntfy's "Users" in the web app settings).
/// </summary>
public sealed class ServerCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ServerUrl { get; set; } = "https://ntfy.sh";

    public AuthMode AuthMode { get; set; } = AuthMode.UsernamePassword;

    public string Token { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>"user@host" style label used in the settings list.</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var host = ServerUrl;
            if (Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
            }
            var who = AuthMode switch
            {
                AuthMode.Token => "token",
                AuthMode.UsernamePassword => string.IsNullOrWhiteSpace(Username) ? "user" : Username,
                _ => "none",
            };
            return $"{who} @ {host}";
        }
    }

    /// <summary>Builds the <c>Authorization</c> header value, or null if not usable.</summary>
    public string? BuildAuthorizationHeader()
    {
        switch (AuthMode)
        {
            case AuthMode.Token when !string.IsNullOrWhiteSpace(Token):
                return $"Bearer {Token.Trim()}";
            case AuthMode.UsernamePassword when !string.IsNullOrWhiteSpace(Username):
                var raw = $"{Username}:{Password}";
                return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            default:
                return null;
        }
    }

    /// <summary>True if two server URLs refer to the same host (ignoring trailing slash/case).</summary>
    public static bool SameServer(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
