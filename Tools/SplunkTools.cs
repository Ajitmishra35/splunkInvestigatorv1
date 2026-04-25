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
    private readonly LogFileService _logService;

    // Fields to strip from AI output (sensitive across any domain)
    private static readonly HashSet<string> _sensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "amount", "user_id", "account_from", "account_to",
        "debtor_account", "creditor_account",
        "debtor_iban", "creditor_iban",
        "debtor_sort_code", "creditor_sort_code",
        "card_number", "pan", "cvv", "ssn", "national_id",
        "email", "phone", "ip_address", "password"
    };

    public SplunkTools(LogFileService logService) => _logService = logService;

    public IEnumerable<AITool> GetTools()
    {
        yield return AIFunctionFactory.Create(DiscoverSchema);
        yield return AIFunctionFactory.Create(SearchByRef);
        yield return AIFunctionFactory.Create(RunQuery);
        yield return AIFunctionFactory.Create(GetStatistics);
        yield return AIFunctionFactory.Create(GetErrors);
        yield return AIFunctionFactory.Create(GetTimeline);
        yield return AIFunctionFactory.Create(GetSampleEntries);
    }

    [Description(@"ALWAYS call this first before any search. 
Returns the schema of every loaded file: which fields exist, which fields are reference keys (like transaction_ref, transfer_ref, loan_ref, claim_ref), and example ref values.
Use this to understand what domain is loaded and what field names to use in queries.")]
    private Task<string> DiscoverSchema()
    {
        var schema = _logService.GetSchemaDescription();
        return Task.FromResult(
            string.IsNullOrWhiteSpace(schema)
                ? "No files loaded yet."
                : schema);
    }

    [Description(@"Search for a specific reference number across ALL loaded domains.
Works for any *_ref field — transaction_ref, transfer_ref, loan_ref, card_ref, claim_ref, or any other.
Call DiscoverSchema first to see what ref fields exist in loaded files.")]
    private async Task<string> SearchByRef(
        [Description("The reference value to find, e.g. TXN-001, TRF-20260425-003, LOAN-2026-441, CARD-REF-009")] string refValue)
    {
        var logs = await _logService.SearchByRefAsync(refValue);
        return logs.Count == 0
            ? $"No entries found for ref: {refValue}"
            : Serialize(logs);
    }

    [Description(@"Run a query against ALL loaded log files.
Supports:
  - key=value for ANY field name (e.g. index=payments, level=ERROR, status=BLOCKED, scheme=SWIFT, loan_status=DEFAULTED, card_type=VISA)
  - Free-text terms that search across all fields and values (e.g. FRAUD, TIMEOUT, REJECTED)
  - Combined: index=transfers status=BLOCKED SWIFT
Call DiscoverSchema first to know which field names are valid for the loaded files.")]
    private async Task<string> RunQuery(
        [Description("Query string. Examples: 'index=transfers level=ERROR', 'status=BLOCKED', 'SWIFT FAILED', 'loan_status=DEFAULTED level=WARN'")] string query)
    {
        var logs = await _logService.SearchBySplQueryAsync(query);
        return logs.Count == 0
            ? $"No results for: {query}"
            : Serialize(logs);
    }

    [Description("Get statistics across all loaded files: total events, errors, warnings, indices, unique refs, and full schema per file.")]
    private async Task<string> GetStatistics()
        => await _logService.GetLogStatisticsAsync();

    [Description("Get all ERROR level events across all loaded files and all domains.")]
    private async Task<string> GetErrors()
    {
        var logs = await _logService.SearchBySplQueryAsync("level=ERROR");
        return logs.Count == 0
            ? "No error events found."
            : Serialize(logs);
    }

    [Description(@"Get a full chronological timeline for a reference number.
Shows every step from start to finish — initiation, validation, processing, completion or failure.
Call DiscoverSchema first to confirm the ref field name in the loaded domain.")]
    private async Task<string> GetTimeline(
        [Description("Reference number, e.g. TXN-20260425-001, TRF-20260425-003, LOAN-2026-441")] string refValue)
    {
        var logs = await _logService.SearchByRefAsync(refValue);
        if (logs.Count == 0)
            return $"No timeline data for: {refValue}";

        // Return clean timeline — only event/status/message/time fields
        var timeline = logs.Select(l => new
        {
            time    = l.Time,
            level   = l.Level,
            @event  = l.Event,
            status  = l.Status,
            message = l.Message,
            // Include any extra domain-specific fields that aren't sensitive
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
            // Try as index first, then as filename substring
            logs = await _logService.SearchBySplQueryAsync($"index={fileOrIndex}");
            if (logs.Count == 0)
                logs = (await _logService.GetAllLogsAsync())
                    .Where(l => l.Source?.Contains(fileOrIndex, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
        }

        var sample = logs.Take(3).ToList();
        return sample.Count == 0
            ? $"No entries found for: {fileOrIndex}"
            : Serialize(sample);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string Serialize(List<GenericLogEntry> logs)
    {
        var safe = logs.Select(l => l.ToCleanDict(_sensitiveFields));
        return JsonSerializer.Serialize(safe, new JsonSerializerOptions { WriteIndented = true });
    }
}
