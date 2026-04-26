using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SplunkInvestigator.Models;
using SplunkInvestigator.Services;

namespace SplunkInvestigator.Tools;

/// <summary>
/// Fully generic agent tools — work for ANY uploaded domain.
/// No hardcoded field names. Agent discovers schema first, then queries.
/// </summary>
public class SplunkTools
{
    private readonly LogFileService       _logService;
    private readonly IVectorStoreService  _vectorStore;
    private readonly EmbeddingService     _embeddingService;

    private static readonly HashSet<string> _sensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "amount", "user_id", "account_from", "account_to",
        "debtor_account", "creditor_account",
        "debtor_iban", "creditor_iban",
        "debtor_sort_code", "creditor_sort_code",
        "card_number", "pan", "cvv", "ssn", "national_id",
        "email", "phone", "ip_address", "password"
    };

    public SplunkTools(
        LogFileService logService,
        IVectorStoreService vectorStore,
        EmbeddingService embeddingService)
    {
        _logService       = logService;
        _vectorStore      = vectorStore;
        _embeddingService = embeddingService;
    }

    public IEnumerable<AITool> GetTools()
    {
        // ── Existing in-memory tools ──────────────────────────────────────────
        yield return AIFunctionFactory.Create(DiscoverSchema);
        yield return AIFunctionFactory.Create(SearchByRef);
        yield return AIFunctionFactory.Create(RunQuery);
        yield return AIFunctionFactory.Create(GetStatistics);
        yield return AIFunctionFactory.Create(GetErrors);
        yield return AIFunctionFactory.Create(GetTimeline);
        yield return AIFunctionFactory.Create(GetSampleEntries);
        yield return AIFunctionFactory.Create(GetTimeOrderedEntries);

        // ── Vector search tools ───────────────────────────────────────────────
        yield return AIFunctionFactory.Create(GetDomainSummary);
        yield return AIFunctionFactory.Create(SearchSemantic);
        yield return AIFunctionFactory.Create(SearchExact);
        yield return AIFunctionFactory.Create(SearchHybrid);
    }

    // ── Existing tools (unchanged) ────────────────────────────────────────────

    [Description(@"ALWAYS call this first before any search.
Returns the schema of every loaded file: which fields exist, which fields are reference keys (like transaction_ref, transfer_ref, loan_ref, claim_ref), and example ref values.
Use this to understand what domain is loaded and what field names to use in queries.")]
    private Task<string> DiscoverSchema()
    {
        var schema = _logService.GetSchemaDescription();
        return Task.FromResult(
            string.IsNullOrWhiteSpace(schema) ? "No files loaded yet." : schema);
    }

    [Description(@"Search for a specific reference number across ALL loaded domains.
Works for any *_ref field — transaction_ref, transfer_ref, loan_ref, card_ref, claim_ref, or any other.
Call DiscoverSchema first to see what ref fields exist in loaded files.")]
    private async Task<string> SearchByRef(
        [Description("The reference value to find, e.g. TXN-001, TRF-20260425-003, LOAN-2026-441, CARD-REF-009")] string refValue)
    {
        var logs = await _logService.SearchByRefAsync(refValue);
        return logs.Count == 0 ? $"No entries found for ref: {refValue}" : Serialize(logs);
    }

    [Description(@"Run a query against ALL loaded log files.
Supports:
  - key=value for ANY field name (e.g. index=payments, level=ERROR, status=BLOCKED, scheme=SWIFT)
  - Free-text terms that search across all fields and values (e.g. FRAUD, TIMEOUT, REJECTED)
  - Combined: index=transfers status=BLOCKED SWIFT
Call DiscoverSchema first to know which field names are valid for the loaded files.")]
    private async Task<string> RunQuery(
        [Description("Query string. Examples: 'index=transfers level=ERROR', 'status=BLOCKED', 'SWIFT FAILED'")] string query)
    {
        var logs = await _logService.SearchBySplQueryAsync(query);
        return logs.Count == 0 ? $"No results for: {query}" : Serialize(logs);
    }

    [Description("Get statistics across all loaded files: total events, errors, warnings, indices, unique refs, and full schema per file.")]
    private async Task<string> GetStatistics()
        => await _logService.GetLogStatisticsAsync();

    [Description("Get all ERROR level events across all loaded files and all domains.")]
    private async Task<string> GetErrors()
    {
        var logs = await _logService.SearchBySplQueryAsync("level=ERROR");
        return logs.Count == 0 ? "No error events found." : Serialize(logs);
    }

    [Description(@"Get a full chronological timeline for a reference number.
Shows every step from start to finish — initiation, validation, processing, completion or failure.
Call DiscoverSchema first to confirm the ref field name in the loaded domain.")]
    private async Task<string> GetTimeline(
        [Description("Reference number, e.g. TXN-20260425-001, TRF-20260425-003, LOAN-2026-441")] string refValue)
    {
        var logs = await _logService.SearchByRefAsync(refValue);
        if (logs.Count == 0) return $"No timeline data for: {refValue}";

        var timeline = logs.Select(l => new
        {
            time    = l.Time,
            level   = l.Level,
            @event  = l.Event,
            status  = l.Status,
            message = l.Message,
            extra   = l.ToCleanDict(_sensitiveFields.Union(
                new[] { "_time", "index", "source", "sourcetype", "host",
                        "namespace", "pod", "container", "node",
                        "logger", "thread", "message", "event", "status", "level" }))
        });

        return JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true });
    }

    [Description(@"Get 3-5 sample log entries from a specific file or index.
Useful to understand the shape of data before querying.
Call DiscoverSchema first to see available file names and indices.")]
    private async Task<string> GetSampleEntries(
        [Description("File name (e.g. transfers_export.json) or index name (e.g. transfers). Leave empty to sample from all.")] string fileOrIndex = "")
    {
        List<GenericLogEntry> logs;

        if (string.IsNullOrWhiteSpace(fileOrIndex))
        {
            logs = await _logService.GetAllLogsAsync();
        }
        else
        {
            logs = await _logService.SearchBySplQueryAsync($"index={fileOrIndex}");
            if (logs.Count == 0)
                logs = (await _logService.GetAllLogsAsync())
                    .Where(l => l.Source?.Contains(fileOrIndex, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
        }

        var sample = logs.Take(3).ToList();
        return sample.Count == 0 ? $"No entries found for: {fileOrIndex}" : Serialize(sample);
    }

    [Description(@"Return matching log entries in chronological order for ops review.
Use this whenever the user asks to see logs, errors, warnings, a service/domain, time-wise entries, or evidence for an analysis.
Supports the same natural query/key=value syntax as RunQuery, for example: 'index=transfers level=ERROR', 'transfers errors', 'status=BLOCKED'.")]
    private async Task<string> GetTimeOrderedEntries(
        [Description("Natural query or key=value filters. Examples: 'index=transfers level=ERROR', 'payment gateway timeout', 'fraud errors'")] string query,
        [Description("Maximum rows to return, default 80")] int maxRows = 80)
    {
        var logs = await _logService.SearchBySplQueryAsync(query);
        if (logs.Count == 0)
            return $"No time-ordered entries found for: {query}";

        var rows = logs
            .OrderBy(l => l.Time)
            .Take(Math.Clamp(maxRows, 1, 200))
            .ToList();

        return Serialize(rows);
    }

    // ── Vector search tools ───────────────────────────────────────────────────

    [Description(@"Get overview of a domain before investigating.
Shows total entries, error count, warning count, time range covered, top events.
Always call this first when starting a new domain investigation.
If the configured vector store is unavailable, use GetStatistics instead.")]
    private async Task<string> GetDomainSummary(
        [Description("Domain name e.g. payments, transfers, loans, cards, fraud")] string domain)
    {
        if (!_vectorStore.IsAvailable)
            return "Vector store unavailable - use GetStatistics for overview.";

        var s = await _vectorStore.GetDomainStatsAsync(domain);
        return $"""
            Domain:       {s.Domain}
            Total entries:{s.TotalEntries:N0}
            Errors:       {s.ErrorCount:N0}
            Warnings:     {s.WarningCount:N0}
            Time range:   {s.EarliestTime ?? "unknown"} → {s.LatestTime ?? "unknown"}
            Top events:   {(s.TopEvents.Count > 0 ? string.Join(", ", s.TopEvents) : "none")}
            """;
    }

    [Description(@"Search using natural language — finds semantically similar log entries.
Use for: suspicious patterns, similar failures, vague queries like 'gateway timeouts' or 'fraud related events'.
Call DiscoverSchema first to confirm domain name.
Falls back gracefully if the configured vector store is unavailable.")]
    private async Task<string> SearchSemantic(
        [Description("Natural language query e.g. 'payment gateway timeout errors' or 'suspicious fraud patterns'")] string query,
        [Description("Domain to search (e.g. payments, transfers). Leave empty to search all.")] string? domain = null,
        [Description("Maximum number of results to return")] int topK = 10)
    {
        if (!_vectorStore.IsAvailable)
            return "Using local search - vector store unavailable. Try RunQuery instead.";

        var vector  = await _embeddingService.EmbedQueryAsync(query);
        var results = await _vectorStore.SearchSemanticAsync(vector, domain, topK);
        return results.Count == 0 ? "No semantic matches found." : Serialize(results);
    }

    [Description(@"Search by exact field values — fast structured search.
Use for: ref numbers (TXN-*, TRF-*, LOAN-*), level=ERROR, status=BLOCKED.
Faster than semantic for structured queries.
Falls back to local search if the configured vector store is unavailable.")]
    private async Task<string> SearchExact(
        [Description("Filter string e.g. 'level=ERROR status=BLOCKED' or a ref number like 'TXN-001'")] string filters)
    {
        if (LooksLikeRef(filters))
        {
            // Ref lookup — try vector store first, fall back to in-memory
            if (_vectorStore.IsAvailable)
            {
                var vResults = await _vectorStore.SearchByRefAsync(filters);
                if (vResults.Count > 0) return Serialize(vResults);
            }
            var mResults = await _logService.SearchByRefAsync(filters);
            return mResults.Count == 0 ? $"No entries found for ref: {filters}" : Serialize(mResults);
        }

        var dict = ParseFilters(filters);
        if (dict.Count == 0) return "No valid filters provided. Use key=value format e.g. 'level=ERROR'.";

        dict.TryGetValue("index", out var domain);
        dict.Remove("index");
        dict.TryGetValue("domain", out var explicitDomain);
        dict.Remove("domain");
        domain ??= explicitDomain;

        if (_vectorStore.IsAvailable)
        {
            var vResults = await _vectorStore.SearchByFiltersAsync(dict, domain);
            if (vResults.Count > 0) return Serialize(vResults);
        }

        // In-memory fallback
        var fallback = await _logService.SearchBySplQueryAsync(filters);
        return fallback.Count == 0 ? $"No exact matches found for: {filters}" : Serialize(fallback);
    }

    [Description(@"Most powerful search — combines natural language with exact filters.
Use for: 'SWIFT failures where status is BLOCKED' or 'fraud events in transfers domain'.
Falls back gracefully if the configured vector store is unavailable.")]
    private async Task<string> SearchHybrid(
        [Description("Natural language query e.g. 'SWIFT payment failures'")] string naturalQuery,
        [Description("Optional exact filters e.g. 'level=ERROR' or 'status=BLOCKED'")] string? filters = null,
        [Description("Domain to search (e.g. payments, transfers). Leave empty to search all.")] string? domain = null,
        [Description("Maximum number of results to return")] int topK = 10)
    {
        if (!_vectorStore.IsAvailable)
            return "Using local search - vector store unavailable. Try RunQuery instead.";

        var vector      = await _embeddingService.EmbedQueryAsync(naturalQuery);
        var filtersDict = filters is not null ? ParseFilters(filters) : new Dictionary<string, string>();
        var results     = await _vectorStore.SearchHybridAsync(vector, filtersDict, domain, topK);
        return results.Count == 0 ? "No hybrid matches found." : Serialize(results);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string Serialize(List<GenericLogEntry> logs)
    {
        var safe = logs.Select(l => l.ToCleanDict(_sensitiveFields));
        return JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool LooksLikeRef(string input)
        => input.Contains('-') && input.Any(char.IsDigit) && !input.Contains('=');

    private static Dictionary<string, string> ParseFilters(string filters)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in filters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.Contains('=')) continue;
            var kv = part.Split('=', 2);
            dict[kv[0].Trim().ToLowerInvariant()] = kv[1].Trim();
        }
        return dict;
    }
}
