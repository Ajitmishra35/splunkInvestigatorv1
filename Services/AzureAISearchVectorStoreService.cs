using System.Text.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using SplunkInvestigator.Models;

namespace SplunkInvestigator.Services;

public sealed class AzureAISearchVectorStoreService : IVectorStoreService
{
    private readonly SearchIndexClient? _indexClient;
    private readonly string _prefix;
    private readonly ILogger<AzureAISearchVectorStoreService> _logger;
    private readonly bool _configured;

    private const int VectorSize = 256;
    private const int UploadBatch = 100;
    private const string VectorField = "vector";
    private const string VectorProfile = "vector-profile";
    private const string VectorAlgorithm = "hnsw-config";

    private static readonly HashSet<string> _sensitiveFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_id", "account_from", "account_to",
        "debtor_account", "creditor_account",
        "debtor_iban", "creditor_iban",
        "debtor_sort_code", "creditor_sort_code",
        "card_number", "pan", "cvv",
        "ssn", "email", "phone", "password", "ip_address"
    };

    public AzureAISearchVectorStoreService(
        IConfiguration config,
        ILogger<AzureAISearchVectorStoreService> logger)
    {
        _logger = logger;

        var section = config.GetSection("AzureAISearch");
        var endpoint = section["Endpoint"];
        var apiKey = section["ApiKey"];
        _prefix = section["IndexPrefix"] ?? "splunk";

        if (IsMissing(endpoint) || IsMissing(apiKey))
        {
            _logger.LogWarning("Azure AI Search not configured - vector search disabled.");
            _configured = false;
            return;
        }

        try
        {
            _indexClient = new SearchIndexClient(new Uri(endpoint!), new AzureKeyCredential(apiKey!));
            _configured = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Azure AI Search client.");
            _configured = false;
        }
    }

    public bool IsAvailable
    {
        get
        {
            if (!_configured || _indexClient is null) return false;

            try
            {
                _indexClient.GetServiceStatistics();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task InitializeCollectionAsync(string domain)
    {
        if (_indexClient is null) return;

        var name = IndexName(domain);

        try
        {
            await _indexClient.GetIndexAsync(name);
            return;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Create below.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure AI Search index check failed for {Domain}", domain);
            return;
        }

        try
        {
            var index = new SearchIndex(name)
            {
                Fields =
                {
                    new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                    new SimpleField("domain", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                    new SimpleField("time", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
                    new SimpleField("level", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                    new SearchableField("event") { IsFilterable = true },
                    new SimpleField("status", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                    new SimpleField("error_code", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                    new SimpleField("ref_value", SearchFieldDataType.String) { IsFilterable = true },
                    new SimpleField("ref_field", SearchFieldDataType.String) { IsFilterable = true },
                    new SimpleField("host", SearchFieldDataType.String) { IsFilterable = true },
                    new SimpleField("pod", SearchFieldDataType.String) { IsFilterable = true },
                    new SimpleField("namespace", SearchFieldDataType.String) { IsFilterable = true },
                    new SearchableField("message"),
                    new SearchableField("full_json"),
                    new VectorSearchField(VectorField, VectorSize, VectorProfile)
                },
                VectorSearch = new VectorSearch
                {
                    Algorithms =
                    {
                        new HnswAlgorithmConfiguration(VectorAlgorithm)
                        {
                            Parameters = new HnswParameters
                            {
                                Metric = VectorSearchAlgorithmMetric.Cosine
                            }
                        }
                    },
                    Profiles =
                    {
                        new VectorSearchProfile(VectorProfile, VectorAlgorithm)
                    }
                }
            };

            await _indexClient.CreateIndexAsync(index);
            _logger.LogInformation("Created Azure AI Search index: {Name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure AI Search index creation failed for {Domain}", domain);
        }
    }

    public async Task UpsertBatchAsync(string domain, List<GenericLogEntry> entries, List<float[]> vectors)
    {
        if (_indexClient is null || entries.Count == 0) return;

        var client = _indexClient.GetSearchClient(IndexName(domain));
        var documents = new List<SearchDocument>(entries.Count);

        for (var i = 0; i < entries.Count && i < vectors.Count; i++)
            documents.Add(ToDocument(domain, entries[i], vectors[i]));

        try
        {
            for (var i = 0; i < documents.Count; i += UploadBatch)
            {
                var batch = documents.Skip(i).Take(UploadBatch).ToList();
                await client.MergeOrUploadDocumentsAsync(batch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure AI Search upsert failed for domain {Domain}", domain);
        }
    }

    public async Task<List<GenericLogEntry>> SearchByRefAsync(string refValue, string? domain = null)
    {
        var filter = $"ref_value eq '{EscapeOData(refValue)}'";
        return await SearchFilterAsync(filter, domain);
    }

    public async Task<List<GenericLogEntry>> SearchByFiltersAsync(
        Dictionary<string, string> filters,
        string? domain = null)
    {
        var filter = string.Join(" and ", filters.Select(kv =>
        {
            var key = kv.Key.Equals("ref_value", StringComparison.OrdinalIgnoreCase)
                ? "ref_value"
                : kv.Key;
            return $"{key} eq '{EscapeOData(kv.Value)}'";
        }));

        return await SearchFilterAsync(filter, domain);
    }

    public async Task<List<GenericLogEntry>> SearchSemanticAsync(
        float[] queryVector,
        string? domain = null,
        int topK = 10)
    {
        return await SearchVectorAsync(queryVector, null, domain, topK);
    }

    public async Task<List<GenericLogEntry>> SearchHybridAsync(
        float[] queryVector,
        Dictionary<string, string> filters,
        string? domain = null,
        int topK = 10)
    {
        var filter = filters.Count == 0
            ? null
            : string.Join(" and ", filters.Select(kv => $"{kv.Key} eq '{EscapeOData(kv.Value)}'"));

        return await SearchVectorAsync(queryVector, filter, domain, topK);
    }

    public async Task<DomainStats> GetDomainStatsAsync(string domain)
    {
        if (_indexClient is null)
            return new DomainStats(domain, 0, 0, 0, null, null, []);

        try
        {
            var client = _indexClient.GetSearchClient(IndexName(domain));
            var options = new SearchOptions { Size = 1000, IncludeTotalCount = true };
            var response = await client.SearchAsync<SearchDocument>("*", options);
            var docs = new List<GenericLogEntry>();

            await foreach (var result in response.Value.GetResultsAsync())
                docs.Add(ToEntry(result.Document));

            var times = docs
                .Select(e => e.Time)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .OrderBy(t => t)
                .ToList();

            var topEvents = docs
                .Select(e => e.Event)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .GroupBy(e => e)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => g.Key!)
                .ToList();

            return new DomainStats(
                domain,
                response.Value.TotalCount ?? docs.Count,
                docs.Count(e => e.Level?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true),
                docs.Count(e => e.Level?.Equals("WARN", StringComparison.OrdinalIgnoreCase) == true),
                times.FirstOrDefault(),
                times.LastOrDefault(),
                topEvents);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure AI Search stats failed for {Domain}", domain);
            return new DomainStats(domain, 0, 0, 0, null, null, []);
        }
    }

    public async Task<List<string>> GetAllDomainsAsync()
    {
        if (_indexClient is null) return [];

        try
        {
            var domains = new List<string>();
            await foreach (var name in _indexClient.GetIndexNamesAsync())
            {
                if (name.StartsWith(_prefix + "-", StringComparison.OrdinalIgnoreCase))
                    domains.Add(name[(_prefix.Length + 1)..]);
            }

            return domains;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure AI Search domain list failed");
            return [];
        }
    }

    private async Task<List<GenericLogEntry>> SearchFilterAsync(string filter, string? domain)
    {
        if (_indexClient is null) return [];

        var results = new List<GenericLogEntry>();
        var indexes = !string.IsNullOrWhiteSpace(domain)
            ? [IndexName(domain)]
            : (await GetAllDomainsAsync()).Select(IndexName).ToList();

        foreach (var index in indexes)
        {
            try
            {
                var client = _indexClient.GetSearchClient(index);
                var options = new SearchOptions { Filter = filter, Size = 200 };
                var response = await client.SearchAsync<SearchDocument>("*", options);

                await foreach (var result in response.Value.GetResultsAsync())
                    results.Add(ToEntry(result.Document));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure AI Search filter query failed for {Index}", index);
            }
        }

        return results;
    }

    private async Task<List<GenericLogEntry>> SearchVectorAsync(
        float[] queryVector,
        string? filter,
        string? domain,
        int topK)
    {
        if (_indexClient is null) return [];

        var results = new List<GenericLogEntry>();
        var indexes = !string.IsNullOrWhiteSpace(domain)
            ? [IndexName(domain)]
            : (await GetAllDomainsAsync()).Select(IndexName).ToList();

        foreach (var index in indexes)
        {
            try
            {
                var client = _indexClient.GetSearchClient(index);
                var vectorQuery = new VectorizedQuery(queryVector)
                {
                    KNearestNeighborsCount = topK
                };
                vectorQuery.Fields.Add(VectorField);

                var options = new SearchOptions
                {
                    Filter = filter,
                    Size = topK,
                    VectorSearch = new VectorSearchOptions()
                };
                options.VectorSearch.Queries.Add(vectorQuery);

                var response = await client.SearchAsync<SearchDocument>("*", options);
                await foreach (var result in response.Value.GetResultsAsync())
                    results.Add(ToEntry(result.Document));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure AI Search vector query failed for {Index}", index);
            }
        }

        return results.Take(topK).ToList();
    }

    private SearchDocument ToDocument(string domain, GenericLogEntry entry, float[] vector)
    {
        var refKv = entry.RawFields
            .FirstOrDefault(kv => kv.Key.EndsWith("_ref", StringComparison.OrdinalIgnoreCase)
                               && !string.IsNullOrWhiteSpace(kv.Value)
                               && kv.Value != "null");

        var safeJson = JsonSerializer.Serialize(
            entry.RawFields
                .Where(kv => !_sensitiveFields.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value));

        return new SearchDocument
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["domain"] = (entry.Index ?? domain).ToLowerInvariant(),
            ["time"] = entry.Time ?? "",
            ["level"] = entry.Level ?? "",
            ["event"] = entry.Event ?? "",
            ["status"] = entry.Status ?? "",
            ["error_code"] = entry.Get("error_code") ?? "",
            ["ref_value"] = refKv.Value ?? "",
            ["ref_field"] = refKv.Key ?? "",
            ["host"] = entry.Host ?? "",
            ["pod"] = entry.Get("pod") ?? "",
            ["namespace"] = entry.Get("namespace") ?? "",
            ["message"] = entry.Message ?? "",
            ["full_json"] = safeJson,
            [VectorField] = vector
        };
    }

    private static GenericLogEntry ToEntry(SearchDocument document)
    {
        var entry = new GenericLogEntry();

        foreach (var (key, value) in document)
        {
            if (key is "id" or VectorField or "full_json") continue;
            entry.RawFields[key] = value?.ToString() ?? "";
        }

        if (document.TryGetValue("full_json", out var raw) && raw is not null)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw.ToString() ?? "");
                if (dict is not null)
                {
                    foreach (var kv in dict)
                        entry.RawFields[kv.Key] = kv.Value;
                }
            }
            catch
            {
                // Keep the indexed fields already restored above.
            }
        }

        return entry;
    }

    private string IndexName(string domain)
        => $"{_prefix}-{domain.Trim().ToLowerInvariant()}";

    private static string EscapeOData(string value)
        => value.Replace("'", "''");

    private static bool IsMissing(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Contains("YOUR-", StringComparison.OrdinalIgnoreCase)
           || value.Contains("<", StringComparison.OrdinalIgnoreCase)
           || value.Contains(">", StringComparison.OrdinalIgnoreCase);
}
