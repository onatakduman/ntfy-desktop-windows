using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace NtfyDesktop.Models;

/// <summary>
/// A message object as delivered by the ntfy streaming JSON endpoint
/// (<c>&lt;server&gt;/&lt;topic&gt;/json</c>), one JSON object per line.
/// See https://docs.ntfy.sh/subscribe/api/#json-message-format.
/// </summary>
public sealed class NtfyMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Message timestamp, unix seconds.</summary>
    [JsonPropertyName("time")]
    public long Time { get; set; }

    /// <summary>"message" | "keepalive" | "open" | "poll_request".</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>1=min, 2=low, 3=default, 4=high, 5=max.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 3;

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("click")]
    public string? Click { get; set; }

    /// <summary>Icon URL shown alongside the message.</summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>"text/markdown" when the message body should be rendered as Markdown.</summary>
    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("attachment")]
    public NtfyAttachment? Attachment { get; set; }

    [JsonPropertyName("actions")]
    public List<NtfyAction>? Actions { get; set; }

    /// <summary>
    /// Server this message arrived from. Not part of the ntfy payload; set
    /// locally so history can be displayed and filtered per server/topic.
    /// </summary>
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = string.Empty;

    // --- Display helpers (computed, not serialized round-trip-critical) ---

    [JsonIgnore]
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();

    [JsonIgnore]
    public string TimeDisplay => Timestamp.ToString("MMM d, HH:mm");

    [JsonIgnore]
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title! : Topic;

    [JsonIgnore]
    public string DisplayBody => Message ?? string.Empty;

    [JsonIgnore]
    public string PriorityLabel => Priority switch
    {
        1 => "Min",
        2 => "Low",
        4 => "High",
        5 => "Max",
        _ => "Default",
    };

    /// <summary>True for priorities that warrant emphasis in the UI / urgent toasts.</summary>
    [JsonIgnore]
    public bool IsHighPriority => Priority >= 4;

    /// <summary>Tags rendered with known emoji shortcodes converted to glyphs.</summary>
    [JsonIgnore]
    public string TagsDisplay
    {
        get
        {
            if (Tags is null || Tags.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var tag in Tags)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(EmojiMap.TryGetValue(tag, out var emoji) ? emoji : $"#{tag}");
            }
            return sb.ToString();
        }
    }

    [JsonIgnore]
    public bool HasTags => Tags is { Count: > 0 };

    [JsonIgnore]
    public bool HasClick => !string.IsNullOrWhiteSpace(Click);

    /// <summary>The click target as an absolute <see cref="Uri"/>, or null if absent/invalid.</summary>
    [JsonIgnore]
    public Uri? ClickUri =>
        Uri.TryCreate(Click, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>True when the body should be rendered as Markdown.</summary>
    [JsonIgnore]
    public bool IsMarkdown =>
        ContentType is not null && ContentType.Contains("markdown", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasAttachment => Attachment is not null && !string.IsNullOrWhiteSpace(Attachment.Url);

    [JsonIgnore]
    public Uri? AttachmentUri =>
        Attachment is not null && Uri.TryCreate(Attachment.Url, UriKind.Absolute, out var u) ? u : null;

    /// <summary>True when the attachment is an image we can preview inline.</summary>
    [JsonIgnore]
    public bool HasImageAttachment =>
        HasAttachment &&
        ((Attachment!.Type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false) ||
         LooksLikeImage(Attachment.Name) || LooksLikeImage(Attachment.Url));

    /// <summary>True when there is an attachment that is not an inline-previewable image.</summary>
    [JsonIgnore]
    public bool HasNonImageAttachment => HasAttachment && !HasImageAttachment;

    [JsonIgnore]
    public string AttachmentLabel =>
        Attachment is null ? string.Empty :
        string.IsNullOrWhiteSpace(Attachment.Name) ? "attachment" : Attachment.Name;

    [JsonIgnore]
    public Uri? IconUri =>
        Uri.TryCreate(Icon, UriKind.Absolute, out var i) ? i : null;

    [JsonIgnore]
    public bool HasIcon => IconUri is not null;

    [JsonIgnore]
    public bool HasActions => Actions is { Count: > 0 };

    private static bool LooksLikeImage(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        s = s.ToLowerInvariant();
        return s.EndsWith(".png") || s.EndsWith(".jpg") || s.EndsWith(".jpeg") ||
               s.EndsWith(".gif") || s.EndsWith(".webp") || s.EndsWith(".bmp");
    }

    /// <summary>
    /// Returns the leading emoji glyphs for any tags that are emoji shortcodes,
    /// to prefix a notification title the way the ntfy clients do.
    /// </summary>
    public string GetEmojiPrefix()
    {
        if (Tags is null)
        {
            return string.Empty;
        }

        var glyphs = Tags
            .Where(t => EmojiMap.ContainsKey(t))
            .Select(t => EmojiMap[t]);
        var joined = string.Concat(glyphs);
        return string.IsNullOrEmpty(joined) ? string.Empty : joined + " ";
    }

    /// <summary>
    /// A small subset of the ntfy emoji shortcode set (the common ones). Unknown
    /// shortcodes fall back to "#tag" display.
    /// </summary>
    private static readonly Dictionary<string, string> EmojiMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warning"] = "⚠️",
        ["white_check_mark"] = "✅",
        ["x"] = "❌",
        ["rotating_light"] = "🚨",
        ["fire"] = "🔥",
        ["tada"] = "🎉",
        ["heavy_check_mark"] = "✔️",
        ["bell"] = "🔔",
        ["no_bell"] = "🔕",
        ["loudspeaker"] = "📢",
        ["computer"] = "💻",
        ["skull"] = "💀",
        ["bug"] = "🐛",
        ["lock"] = "🔒",
        ["key"] = "🔑",
        ["envelope"] = "✉️",
        ["partying_face"] = "🥳",
        ["+1"] = "👍",
        ["-1"] = "👎",
        ["heart"] = "❤️",
        ["green_circle"] = "🟢",
        ["red_circle"] = "🔴",
        ["question"] = "❓",
        ["information_source"] = "ℹ️",
    };
}

/// <summary>A file attached to a message (uploaded to ntfy or linked by URL).</summary>
public sealed class NtfyAttachment
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

/// <summary>An action button attached to a message (view/http/broadcast).</summary>
public sealed class NtfyAction
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>HTTP method for "http" actions.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("clear")]
    public bool Clear { get; set; }
}
