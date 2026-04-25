using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplunkInvestigator.Models;

/// <summary>
/// Generic log entry — works for ANY domain (payments, transfers, loans, cards, etc.)
/// Every field lands in RawFields dictionary. Nothing is hardcoded.
/// Common fields are exposed as convenience properties that read from RawFields.
/// </summary>
public class GenericLogEntry
{
    /// <summary>
    /// ALL fields from the JSON — the source of truth.
    /// Key = field name (lowercase), Value = raw string value.
    /// </summary>
    public Dictionary<string, string> RawFields { get; set; } = new();

    // ── Convenience accessors (read from RawFields, never hardcoded) ─────────
    public string? Get(string key)
        => RawFields.TryGetValue(key.ToLowerInvariant(), out var v) ? v : null;

    public string? Time    => Get("_time");
    public string? Index   => Get("index");
    public string? Source  => Get("source");
    public string? Host    => Get("host");
    public string? Level   => Get("level");
    public string? Message => Get("message");
    public string? Event   => Get("event");
    public string? Status  => Get("status");

    /// <summary>
    /// Any field ending in _ref is treated as a reference key.
    /// Covers: transaction_ref, transfer_ref, loan_ref, card_ref, claim_ref — anything.
    /// </summary>
    public IEnumerable<string> AllRefs =>
        RawFields
            .Where(kv => kv.Key.EndsWith("_ref", StringComparison.OrdinalIgnoreCase)
                      && !string.IsNullOrWhiteSpace(kv.Value)
                      && kv.Value != "null")
            .Select(kv => kv.Value);

    /// <summary>Full-text search across ALL fields.</summary>
    public bool ContainsText(string term)
    {
        var t = term.ToLowerInvariant();
        foreach (var kv in RawFields)
        {
            if (kv.Key.Contains(t, StringComparison.OrdinalIgnoreCase))   return true;
            if (kv.Value.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Filter by key=value (case-insensitive on both key and value).</summary>
    public bool Matches(string key, string value)
    {
        var v = Get(key);
        return v != null && v.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Serialize only non-null fields for sending to AI (clean output).</summary>
    public Dictionary<string, string> ToCleanDict(IEnumerable<string>? excludeKeys = null)
    {
        var exclude = excludeKeys?.Select(k => k.ToLowerInvariant()).ToHashSet()
                      ?? new HashSet<string>();
        return RawFields
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value)
                      && kv.Value != "null"
                      && !exclude.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}

/// <summary>
/// Tracks schema discovered from a loaded file.
/// Agent uses this to know what fields are available in each domain.
/// </summary>
public class DomainSchema
{
    public string FileName { get; set; } = string.Empty;
    public string? Index { get; set; }
    public List<string> Fields { get; set; } = [];
    public List<string> RefFields { get; set; } = [];   // fields ending in _ref
    public List<string> SampleRefs { get; set; } = [];  // a few example values
    public int EntryCount { get; set; }
}

/// <summary>Upload tracking record shown in the sidebar.</summary>
public class UploadedLogFile
{
    public string FileName { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;
    public string? ErrorMessage { get; set; }
    public bool Success => ErrorMessage is null;
}

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsToolCall { get; set; }
    public string? ToolName { get; set; }
    public string? GridTitle { get; set; }
    public List<GenericLogEntry> LogRows { get; set; } = [];
}
