using System.Text.Json;
using Google.Protobuf.Collections;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SplunkInvestigator.Models;

namespace SplunkInvestigator.Services;

/// <summary>
/// Qdrant Cloud implementation of IVectorStoreService.
/// Collection name pattern: {prefix}-{domain}  e.g. splunk-payments
/// Silent fallback: any Qdrant error returns empty list — never throws.
/// </summary>
public sealed class QdrantVectorStoreService : IVectorStoreService
{
    private readonly QdrantClient? _client;
    private readonly string _prefix;
    private readonly ILogger<QdrantVectorStoreService> _logger;
    private readonly bool _configured;

    private const int VectorSize  = 256;
    private const int UpsertBatch = 100;

    private static readonly HashSet<string> _sensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "amount", "user_id", "account_from", "account_to",
        "debtor_account", "creditor_account",
        "debtor_iban", "creditor_iban",
        "debtor_sort_code", "creditor_sort_code",
        "card_number", "pan", "cvv",
        "ssn", "email", "phone", "password", "ip_address"
    };

    public QdrantVectorStoreService(IConfiguration config, ILogger<QdrantVectorStoreService> logger)
    {
        _logger = logger;

        var section  = config.GetSection("Qdrant");
        var endpoint = section["Endpoint"];
        var apiKey   = section["ApiKey"];
        _prefix      = section["CollectionPrefix"] ?? "splunk";

        if (string.IsNullOrWhiteSpace(endpoint) ||
            endpoint.StartsWith('[') ||          // still a placeholder
            string.IsNullOrWhiteSpace(apiKey)   ||
            apiKey.StartsWith('['))
        {
            _logger.LogWarning("Qdrant not configured — vector search disabled.");
            _configured = false;
            return;
        }

        try
        {
            _client = new QdrantClient(
                host:   new Uri(endpoint).Host,
                https:  true,
                apiKey: apiKey,
                port:   6334);

            _configured = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Qdrant client.");
            _configured = false;
        }
    }

    // ── IsAvailable ───────────────────────────────────────────────────────────

    public bool IsAvailable
    {
        get
        {
            if (!_configured || _client is null) return false;
            try
            {
                // Synchronous health check — intentionally blocking here
                // (property contract; callers should not await)
                _client.HealthAsync().GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // ── Collection management ─────────────────────────────────────────────────

    public async Task InitializeCollectionAsync(string domain)
    {
        if (_client is null) return;
        var name = CollectionName(domain);

        try
        {
            var exists = await _client.CollectionExistsAsync(name);
            if (exists) return;

            await _client.CreateCollectionAsync(name,
                new VectorParams
                {
                    Size     = VectorSize,
                    Distance = Distance.Cosine
                });

            _logger.LogInformation("Created Qdrant collection: {Name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializeCollection failed for domain {Domain}", domain);
        }
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    public async Task UpsertBatchAsync(string domain,
                                       List<GenericLogEntry> entries,
                                       List<float[]> vectors)
    {
        if (_client is null || entries.Count == 0) return;
        var name = CollectionName(domain);

        try
        {
            var points = new List<PointStruct>(entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                // Find first *_ref field
                var refKv = entry.RawFields
                    .FirstOrDefault(kv => kv.Key.EndsWith("_ref",
                        StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(kv.Value)
                        && kv.Value != "null");

                var safeJson = JsonSerializer.Serialize(
                    entry.RawFields
                         .Where(kv => !_sensitiveFields.Contains(kv.Key))
                         .ToDictionary(kv => kv.Key, kv => kv.Value));

                var payload = new MapField<string, Value>
                {
                    ["domain"]     = entry.Index     ?? domain,
                    ["time"]       = entry.Time      ?? "",
                    ["level"]      = entry.Level     ?? "",
                    ["event"]      = entry.Event     ?? "",
                    ["status"]     = entry.Status    ?? "",
                    ["error_code"] = entry.Get("error_code") ?? "",
                    ["ref_value"]  = refKv.Value     ?? "",
                    ["ref_field"]  = refKv.Key       ?? "",
                    ["host"]       = entry.Host      ?? "",
                    ["pod"]        = entry.Get("pod")       ?? "",
                    ["namespace"]  = entry.Get("namespace") ?? "",
                    ["message"]    = entry.Message   ?? "",
                    ["full_json"]  = safeJson
                };

                points.Add(new PointStruct
                {
                    Id      = new PointId { Uuid = Guid.NewGuid().ToString() },
                    Vectors = new Vectors { Vector = new Vector { Data = { vectors[i] } } },
                    Payload = { payload }
                });
            }

            // Upsert in batches
            for (int i = 0; i < points.Count; i += UpsertBatch)
            {
                var batch = points.Skip(i).Take(UpsertBatch).ToList();
                await _client.UpsertAsync(name, batch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertBatch failed for domain {Domain}", domain);
        }
    }

    // ── Search: by ref ────────────────────────────────────────────────────────

    public async Task<List<GenericLogEntry>> SearchByRefAsync(
        string refValue, string? domain = null)
    {
        if (_client is null) return [];
        try
        {
            var filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key   = "ref_value",
                            Match = new Match { Text = refValue }
                        }
                    }
                }
            };

            if (domain is not null)
                filter.Must.Add(DomainCondition(domain));

            var result = await _client.ScrollAsync(
                CollectionName(domain ?? _prefix),
                filter,
                limit: 200,
                payloadSelector: true);

            return result.Result.Select(PayloadToEntry).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchByRef failed for {Ref}", refValue);
            return [];
        }
    }

    // ── Search: by filters ────────────────────────────────────────────────────

    public async Task<List<GenericLogEntry>> SearchByFiltersAsync(
        Dictionary<string, string> filters, string? domain = null)
    {
        if (_client is null) return [];
        try
        {
            var must = filters
                .Select(kv => new Condition
                {
                    Field = new FieldCondition
                    {
                        Key   = kv.Key,
                        Match = new Match { Keyword = kv.Value }
                    }
                })
                .ToList();

            if (domain is not null)
                must.Add(DomainCondition(domain));

            var filter = new Filter();
            filter.Must.AddRange(must);

            var collections = domain is not null
                ? [CollectionName(domain)]
                : await GetCollectionNamesAsync();

            var results = new List<GenericLogEntry>();
            foreach (var col in collections)
            {
                var scroll = await _client.ScrollAsync(
                    col, filter, limit: 200, payloadSelector: true);
                results.AddRange(scroll.Result.Select(PayloadToEntry));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchByFilters failed");
            return [];
        }
    }

    // ── Search: semantic ─────────────────────────────────────────────────────

    public async Task<List<GenericLogEntry>> SearchSemanticAsync(
        float[] queryVector, string? domain = null, int topK = 10)
    {
        if (_client is null) return [];
        try
        {
            Filter? filter = domain is not null
                ? new Filter { Must = { DomainCondition(domain) } }
                : null;

            var collections = domain is not null
                ? [CollectionName(domain)]
                : await GetCollectionNamesAsync();

            var results = new List<GenericLogEntry>();
            foreach (var col in collections)
            {
                var hits = await _client.SearchAsync(
                    col,
                    queryVector,
                    filter:      filter,
                    limit:       (ulong)topK,
                    payloadSelector: true);

                results.AddRange(hits.Select(h => PayloadToEntry(h)));
            }

            return results
                .Take(topK)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchSemantic failed");
            return [];
        }
    }

    // ── Search: hybrid ────────────────────────────────────────────────────────

    public async Task<List<GenericLogEntry>> SearchHybridAsync(
        float[] queryVector,
        Dictionary<string, string> filters,
        string? domain = null,
        int topK = 10)
    {
        if (_client is null) return [];
        try
        {
            var must = filters
                .Select(kv => new Condition
                {
                    Field = new FieldCondition
                    {
                        Key   = kv.Key,
                        Match = new Match { Keyword = kv.Value }
                    }
                })
                .ToList();

            if (domain is not null)
                must.Add(DomainCondition(domain));

            var filter = new Filter();
            filter.Must.AddRange(must);

            var collections = domain is not null
                ? [CollectionName(domain)]
                : await GetCollectionNamesAsync();

            var results = new List<GenericLogEntry>();
            foreach (var col in collections)
            {
                var hits = await _client.SearchAsync(
                    col,
                    queryVector,
                    filter:      filter,
                    limit:       (ulong)topK,
                    payloadSelector: true);

                results.AddRange(hits.Select(h => PayloadToEntry(h)));
            }

            return results
                .Take(topK)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchHybrid failed");
            return [];
        }
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public async Task<DomainStats> GetDomainStatsAsync(string domain)
    {
        if (_client is null)
            return new DomainStats(domain, 0, 0, 0, null, null, []);

        var name = CollectionName(domain);
        try
        {
            var info   = await _client.GetCollectionInfoAsync(name);
            var total  = (long)info.PointsCount;

            var errorCount = await CountByLevelAsync(name, "ERROR");
            var warnCount  = await CountByLevelAsync(name, "WARN");

            // Sample up to 20 points to find time range and top events
            var sample = await _client.ScrollAsync(name, limit: 20, payloadSelector: true);
            var times  = sample.Result
                .Select(p => p.Payload.TryGetValue("time", out var t) ? t.StringValue : null)
                .Where(t => !string.IsNullOrEmpty(t))
                .OrderBy(t => t)
                .ToList();

            var topEvents = sample.Result
                .Select(p => p.Payload.TryGetValue("event", out var e) ? e.StringValue : null)
                .Where(e => !string.IsNullOrEmpty(e))
                .GroupBy(e => e)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key!)
                .ToList();

            return new DomainStats(
                Domain:       domain,
                TotalEntries: total,
                ErrorCount:   errorCount,
                WarningCount: warnCount,
                EarliestTime: times.FirstOrDefault(),
                LatestTime:   times.LastOrDefault(),
                TopEvents:    topEvents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDomainStats failed for {Domain}", domain);
            return new DomainStats(domain, 0, 0, 0, null, null, []);
        }
    }

    public async Task<List<string>> GetAllDomainsAsync()
    {
        if (_client is null) return [];
        try
        {
            var collections = await _client.ListCollectionsAsync();
            return collections
                .Where(c => c.StartsWith(_prefix + "-", StringComparison.OrdinalIgnoreCase))
                .Select(c => c[(_prefix.Length + 1)..])
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllDomains failed");
            return [];
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CollectionName(string domain)
        => $"{_prefix}-{domain.ToLowerInvariant()}";

    private static Condition DomainCondition(string domain) => new()
    {
        Field = new FieldCondition
        {
            Key   = "domain",
            Match = new Match { Keyword = domain.ToLowerInvariant() }
        }
    };

    private async Task<long> CountByLevelAsync(string collection, string level)
    {
        try
        {
            var filter = new Filter
            {
                Must =
                {
                    new Condition
                    {
                        Field = new FieldCondition
                        {
                            Key   = "level",
                            Match = new Match { Keyword = level }
                        }
                    }
                }
            };

            var result = await _client!.CountAsync(collection, filter);
            return (long)result;
        }
        catch
        {
            return 0;
        }
    }

    private async Task<List<string>> GetCollectionNamesAsync()
    {
        try
        {
            var all = await _client!.ListCollectionsAsync();
            return all
                .Where(c => c.StartsWith(_prefix + "-", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static GenericLogEntry PayloadToEntry(ScoredPoint point)
        => PayloadToEntry(point.Payload);

    private static GenericLogEntry PayloadToEntry(RetrievedPoint point)
        => PayloadToEntry(point.Payload);

    private static GenericLogEntry PayloadToEntry(MapField<string, Value> payload)
    {
        var entry = new GenericLogEntry();

        // Restore standard fields
        foreach (var kv in payload)
        {
            if (kv.Key == "full_json") continue;
            entry.RawFields[kv.Key] = kv.Value.StringValue ?? "";
        }

        // Overlay with full_json to restore any non-sensitive RawFields
        if (payload.TryGetValue("full_json", out var jsonVal) &&
            !string.IsNullOrWhiteSpace(jsonVal.StringValue))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    jsonVal.StringValue);
                if (dict is not null)
                    foreach (var kv in dict)
                        entry.RawFields[kv.Key] = kv.Value;
            }
            catch { /* corrupt payload — keep what we have */ }
        }

        return entry;
    }
}
