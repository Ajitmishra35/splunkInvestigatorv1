using Microsoft.Extensions.AI;
using SplunkInvestigator.Models;

namespace SplunkInvestigator.Services;

/// <summary>
/// Wraps IEmbeddingGenerator — the Microsoft.Extensions.AI / Agent Framework
/// standard abstraction. Swap the registered implementation in Program.cs
/// without touching this class.
/// </summary>
public class EmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly int _dimensions;
    private readonly ILogger<EmbeddingService> _logger;

    private const int BatchSize = 100;

    private static readonly HashSet<string> _sensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_id", "account_from", "account_to",
        "debtor_account", "creditor_account",
        "debtor_iban", "creditor_iban",
        "debtor_sort_code", "creditor_sort_code",
        "card_number", "pan", "cvv",
        "ssn", "email", "phone", "password", "ip_address"
    };

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IConfiguration config,
        ILogger<EmbeddingService> logger)
    {
        _generator  = generator;
        _dimensions = config.GetSection("AzureOpenAI").GetValue<int>("EmbeddingDimensions", 256);
        _logger     = logger;
    }

    /// <summary>
    /// Embed a list of log entries in batches of 100.
    /// Reports (done, total) after each batch completes.
    /// </summary>
    public async Task<List<float[]>> EmbedBatchAsync(
        List<GenericLogEntry> entries,
        IProgress<(int done, int total)>? progress = null)
    {
        var results = new List<float[]>(entries.Count);
        var total   = entries.Count;

        for (int i = 0; i < total; i += BatchSize)
        {
            var batch = entries.Skip(i).Take(BatchSize).ToList();
            var texts = batch.Select(BuildEmbedText).ToList();

            var options    = new EmbeddingGenerationOptions { Dimensions = _dimensions };
            var embeddings = await _generator.GenerateAsync(texts, options);

            foreach (var embedding in embeddings)
                results.Add(embedding.Vector.ToArray());

            progress?.Report((Math.Min(i + BatchSize, total), total));
        }

        return results;
    }

    /// <summary>Embed a single natural-language query string.</summary>
    public async Task<float[]> EmbedQueryAsync(string queryText)
    {
        var options = new EmbeddingGenerationOptions { Dimensions = _dimensions };
        var result  = await _generator.GenerateAsync([queryText], options);
        return result[0].Vector.ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildEmbedText(GenericLogEntry entry)
    {
        var parts = new List<string>();

        // Core semantic fields first — highest signal for the model
        if (!string.IsNullOrWhiteSpace(entry.Event))   parts.Add(entry.Event);
        if (!string.IsNullOrWhiteSpace(entry.Status))  parts.Add(entry.Status);
        if (!string.IsNullOrWhiteSpace(entry.Level))   parts.Add(entry.Level);
        if (!string.IsNullOrWhiteSpace(entry.Message)) parts.Add(entry.Message);

        // All remaining non-sensitive RawFields values
        foreach (var kv in entry.RawFields)
        {
            if (_sensitiveFields.Contains(kv.Key))                       continue;
            if (string.IsNullOrWhiteSpace(kv.Value) || kv.Value == "null") continue;

            // Skip fields already added above to avoid duplication
            var key = kv.Key;
            if (key is "event" or "status" or "level" or "message")     continue;

            parts.Add(kv.Value);
        }

        return string.Join(" ", parts);
    }
}
