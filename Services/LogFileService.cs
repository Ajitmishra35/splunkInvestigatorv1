using System.Text.Json;
using SplunkInvestigator.Models;

namespace SplunkInvestigator.Services;

/// <summary>
/// Generic log store — domain agnostic.
/// Parses ANY Splunk JSON export into GenericLogEntry (key-value bag).
/// Automatically discovers schema so the agent knows what fields exist.
/// </summary>
public class LogFileService
{
    private readonly string _logsFolder;
    private readonly ILogger<LogFileService> _logger;
    private readonly IVectorStoreService _vectorStore;
    private readonly EmbeddingService _embeddingService;

    private List<GenericLogEntry>? _diskLogs;
    private readonly HashSet<string> _indexedDiskFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<GenericLogEntry>> _uploadedLogs = new();
    private readonly List<UploadedLogFile> _uploadHistory = new();
    private readonly Dictionary<string, DomainSchema> _schemas = new();

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public LogFileService(
        IConfiguration config,
        ILogger<LogFileService> logger,
        IVectorStoreService vectorStore,
        EmbeddingService embeddingService)
    {
        _logger           = logger;
        _vectorStore      = vectorStore;
        _embeddingService = embeddingService;

        var folder = config["SplunkSettings:LogsFolder"] ?? "SampleLogs";
        _logsFolder = Path.IsPathRooted(folder)
            ? folder
            : Path.Combine(AppContext.BaseDirectory, folder);
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public IReadOnlyList<UploadedLogFile> UploadHistory => _uploadHistory;
    public IReadOnlyDictionary<string, DomainSchema> Schemas => _schemas;

    /// <summary>All entries from disk + uploads merged.</summary>
    public async Task<List<GenericLogEntry>> GetAllLogsAsync()
    {
        var disk = await GetDiskLogsAsync();
        if (_uploadedLogs.Count == 0) return disk;
        var merged = new List<GenericLogEntry>(disk);
        foreach (var batch in _uploadedLogs.Values)
            merged.AddRange(batch);
        return merged;
    }

    public Task InitializeSampleLogsAsync()
        => GetDiskLogsAsync();

    /// <summary>
    /// Upload a JSON file from the browser.
    /// Parses into GenericLogEntry — works for ANY domain.
    /// Discovers schema automatically, then indexes into the configured vector store if available.
    /// </summary>
    public async Task<UploadedLogFile> AddUploadedLogsAsync(
        string fileName,
        Stream stream,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var record = new UploadedLogFile { FileName = fileName };
        List<GenericLogEntry> entries = [];

        try
        {
            // ── Stage 1: Parse ────────────────────────────────────────────────
            progress?.Report($"Parsing file...");

            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);

            entries = ParseGeneric(content);

            if (entries.Count == 0)
                throw new InvalidDataException("File parsed but contained 0 log entries.");

            _uploadedLogs[fileName] = entries;
            record.EntryCount = entries.Count;
            _schemas[fileName] = BuildSchema(fileName, entries);

            progress?.Report($"Parsing file... {entries.Count} entries found");
            _logger.LogInformation("Uploaded {Count} entries from {File}", entries.Count, fileName);
        }
        catch (Exception ex)
        {
            record.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to parse uploaded file: {File}", fileName);
            _uploadHistory.Insert(0, record);
            return record;
        }

        // ── Stages 2-4: Vector store indexing (best-effort) ──────────────────
        if (_vectorStore.IsAvailable)
        {
            try
            {
                var domain = entries.FirstOrDefault(e => e.Index is not null)?.Index
                             ?? Path.GetFileNameWithoutExtension(fileName);

                cancellationToken.ThrowIfCancellationRequested();

                // Stage 2: Init collection
                progress?.Report("Initializing collection...");
                await _vectorStore.InitializeCollectionAsync(domain);

                cancellationToken.ThrowIfCancellationRequested();

                // Stage 3: Embed
                var batchProgress = new Progress<(int done, int total)>(p =>
                    progress?.Report($"Embedding batch {p.done} of {p.total}..."));

                var vectors = await _embeddingService.EmbedBatchAsync(entries, batchProgress);

                cancellationToken.ThrowIfCancellationRequested();

                // Stage 4: Upsert
                progress?.Report("Storing in vector search...");
                await _vectorStore.UpsertBatchAsync(domain, entries, vectors);

                progress?.Report($"Done — {entries.Count} entries indexed and searchable");
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Indexed {Count} entries into vector store domain '{Domain}'",
                        entries.Count, domain);
            }
            catch (OperationCanceledException)
            {
                progress?.Report("Upload cancelled.");
                _logger.LogWarning("Vector indexing cancelled for {File}", fileName);
            }
            catch (Exception ex)
            {
                // Never fail the upload — in-memory search still works
                progress?.Report("Vector indexing failed - using local search.");
                _logger.LogError(ex, "Vector store indexing failed for {File}", fileName);
            }
        }

        _uploadHistory.Insert(0, record);
        return record;
    }

    public void RemoveUploadedFile(string fileName)
    {
        _uploadedLogs.Remove(fileName);
        _schemas.Remove(fileName);
        var h = _uploadHistory.FirstOrDefault(x => x.FileName == fileName);
        if (h is not null) _uploadHistory.Remove(h);
    }

    // ── Search API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Search by any *_ref field value — works for transaction_ref, transfer_ref,
    /// loan_ref, card_ref, claim_ref — any domain, automatically.
    /// </summary>
    public async Task<List<GenericLogEntry>> SearchByRefAsync(string refValue)
    {
        var logs = await GetAllLogsAsync();
        return logs
            .Where(l => l.AllRefs.Any(r =>
                r.Contains(refValue, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(l => l.Time)
            .ToList();
    }

    /// <summary>
    /// Generic SPL-style query.
    /// Supports key=value for ANY field name — not just hardcoded ones.
    /// Also supports free-text terms that match any field/value.
    /// </summary>
    public async Task<List<GenericLogEntry>> SearchBySplQueryAsync(string spl)
    {
        var logs = await GetAllLogsAsync();

        var filters  = new Dictionary<string, string>();
        var freeTerms = new List<string>();
        var tokens = TokenizeQuery(spl).ToList();
        var allFields = logs.SelectMany(l => l.RawFields.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var consumed = AddNaturalFieldFilters(tokens, allFields, filters);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed.Contains(i)) continue;

            var part = tokens[i];
            if (part.Contains('='))
            {
                var kv = part.Split('=', 2);
                var key = ResolveFieldName(kv[0].Trim('"', '\''), allFields) ?? kv[0].Trim('"', '\'').ToLowerInvariant();
                filters[key] = NormalizeQueryValue(kv[1]);
            }
            else if (part.Length > 1)
            {
                var term = NormalizeQueryValue(part).ToLowerInvariant();
                if (term is "error" or "errors")
                    filters["level"] = "error";
                else if (term is "warn" or "warning" or "warnings")
                    filters["level"] = "warn";
                else
                    freeTerms.Add(term);
            }
        }

        var results = logs.AsEnumerable();

        // Apply key=value filters against RawFields — works for ANY field
        foreach (var (key, value) in filters)
            results = results.Where(l => l.Matches(key, value));

        // Free-text: match any field name or value
        foreach (var term in freeTerms)
            results = results.Where(l => l.ContainsText(term));

        return results.OrderBy(l => l.Time).ToList();
    }

    /// <summary>
    /// UI-facing search for natural requests like "show all logs transfer-service container".
    /// Command words are ignored, while useful terms and key=value filters are applied.
    /// </summary>
    public async Task<List<GenericLogEntry>> SearchForDisplayAsync(string query)
    {
        var logs = await GetAllLogsAsync();
        var filters = new Dictionary<string, string>();
        var freeTerms = new List<string>();
        var stopWords = new HashSet<string>
        {
            "show", "all", "log", "logs", "data", "record", "records",
            "entry", "entries", "event", "events", "please", "get", "give",
            "me", "list", "display", "find", "search", "for", "the", "and",
            "there", "their", "analysis", "analyze", "analyse", "where",
            "is", "are", "equals", "equal", "eq", "with", "having"
        };
        var tokens = TokenizeQuery(query).ToList();
        var allFields = logs.SelectMany(l => l.RawFields.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var consumed = AddNaturalFieldFilters(tokens, allFields, filters);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed.Contains(i)) continue;

            var part = tokens[i];
            if (part.Contains('='))
            {
                var kv = part.Split('=', 2);
                var key = ResolveFieldName(kv[0].Trim('"', '\''), allFields) ?? kv[0].Trim('"', '\'').ToLowerInvariant();
                filters[key] = NormalizeQueryValue(kv[1]);
            }
            else if (part.Length > 1)
            {
                var term = NormalizeQueryValue(part).ToLowerInvariant();
                if (term is "error" or "errors")
                    filters["level"] = "error";
                else if (term is "warn" or "warning" or "warnings")
                    filters["level"] = "warn";
                else if (!stopWords.Contains(term))
                    freeTerms.Add(term);
            }
        }

        if (filters.Count == 0 && freeTerms.Count == 0)
            return [];

        var results = logs.AsEnumerable();

        foreach (var (key, value) in filters)
            results = results.Where(l => l.Matches(key, value));

        foreach (var term in freeTerms)
            results = results.Where(l => l.ContainsText(term));

        return results.OrderBy(l => l.Time).ToList();
    }

    /// <summary>
    /// Statistics + schema summary — tells the agent what domains and fields are loaded.
    /// </summary>
    public async Task<string> GetLogStatisticsAsync()
    {
        var logs = await GetAllLogsAsync();
        if (logs.Count == 0) return "No logs loaded.";

        var errors  = logs.Count(l => l.Level?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true);
        var warns   = logs.Count(l => l.Level?.Equals("WARN",  StringComparison.OrdinalIgnoreCase) == true);
        var indices = logs.Select(l => l.Index).Where(i => i != null).Distinct().OrderBy(x => x);
        var allRefs = logs.SelectMany(l => l.AllRefs).Distinct().Count();

        var lines = new List<string>
        {
            $"Total: {logs.Count} events | Errors: {errors} | Warnings: {warns} | Unique refs: {allRefs}",
            $"Indices loaded: {string.Join(", ", indices)}"
        };

        // Show discovered schema per file
        foreach (var schema in _schemas.Values)
        {
            lines.Add($"\n[{schema.FileName}] index={schema.Index ?? "unknown"} | {schema.EntryCount} entries");
            lines.Add($"  Fields: {string.Join(", ", schema.Fields)}");
            if (schema.RefFields.Count > 0)
                lines.Add($"  Ref fields: {string.Join(", ", schema.RefFields)}");
            if (schema.SampleRefs.Count > 0)
                lines.Add($"  Sample refs: {string.Join(", ", schema.SampleRefs.Take(3))}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Returns just the schema info — used by the agent to discover
    /// what fields are available in each loaded file before querying.
    /// </summary>
    public string GetSchemaDescription()
    {
        if (_schemas.Count == 0)
            return "No files uploaded yet. Using SampleLogs folder only.";

        var parts = _schemas.Values.Select(s =>
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"File: {s.FileName} | Index: {s.Index ?? "unknown"} | Entries: {s.EntryCount}");
            sb.AppendLine($"  Available fields: {string.Join(", ", s.Fields)}");
            if (s.RefFields.Any())
                sb.AppendLine($"  Reference fields: {string.Join(", ", s.RefFields)}");
            if (s.SampleRefs.Any())
                sb.AppendLine($"  Example ref values: {string.Join(", ", s.SampleRefs)}");
            return sb.ToString();
        });

        return string.Join("\n", parts);
    }

    // ── Cache ─────────────────────────────────────────────────────────────────

    public void InvalidateDiskCache()
    {
        _diskLogs = null;
        _schemas.Clear();
    }

    public void InvalidateCache()
    {
        _diskLogs = null;
        _uploadedLogs.Clear();
        _uploadHistory.Clear();
        _schemas.Clear();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task<List<GenericLogEntry>> GetDiskLogsAsync()
    {
        if (_diskLogs is not null) return _diskLogs;

        var result = new List<GenericLogEntry>();
        if (!Directory.Exists(_logsFolder))
        {
            _diskLogs = result;
            return result;
        }

        foreach (var file in Directory.GetFiles(_logsFolder, "*.json"))
        {
            try
            {
                var json  = await File.ReadAllTextAsync(file);
                var entries = ParseGeneric(json);
                result.AddRange(entries);

                var fileName = Path.GetFileName(file);
                _schemas[fileName] = BuildSchema(fileName, entries);
                await IndexEntriesAsync(fileName, entries);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse disk log: {File}", file);
            }
        }

        _diskLogs = result;
        return result;
    }

    private async Task IndexEntriesAsync(string fileName, List<GenericLogEntry> entries)
    {
        if (!_vectorStore.IsAvailable || entries.Count == 0 || !_indexedDiskFiles.Add(fileName))
            return;

        try
        {
            var domain = entries.FirstOrDefault(e => e.Index is not null)?.Index
                         ?? Path.GetFileNameWithoutExtension(fileName);

            await _vectorStore.InitializeCollectionAsync(domain);
            var vectors = await _embeddingService.EmbedBatchAsync(entries);
            await _vectorStore.UpsertBatchAsync(domain, entries, vectors);

            _logger.LogInformation(
                "Indexed {Count} sample log entries into vector store domain '{Domain}'",
                entries.Count,
                domain);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sample log vector indexing failed for {File}", fileName);
        }
    }

    /// <summary>
    /// Parse JSON into GenericLogEntry (key-value bag).
    /// Handles: JSON array, NDJSON, single object, nested arrays inside wrapper objects.
    /// </summary>
    private static List<GenericLogEntry> ParseGeneric(string content)
    {
        var trimmed = content.TrimStart();
        var result  = new List<GenericLogEntry>();

        if (trimmed.StartsWith('['))
        {
            // JSON array — standard Splunk export
            using var doc = JsonDocument.Parse(content);
            foreach (var element in doc.RootElement.EnumerateArray())
                result.Add(FlattenElement(element));
        }
        else if (trimmed.StartsWith('{'))
        {
            // Could be single object OR Splunk wrapper {"results":[...]}
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Try known Splunk wrapper keys
            foreach (var key in new[] { "results", "events", "rows", "hits", "data" })
            {
                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                        result.Add(FlattenElement(el));
                    return result;
                }
            }

            // Single object
            result.Add(FlattenElement(root));
        }
        else
        {
            // NDJSON (one JSON object per line)
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim().TrimEnd(',');
                if (t.StartsWith('{'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(t);
                        result.Add(FlattenElement(doc.RootElement));
                    }
                    catch { /* skip bad lines */ }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Flatten a JsonElement into a string dictionary.
    /// Nested objects are dot-notation flattened (e.g. kubernetes.pod → kubernetes.pod).
    /// Arrays are joined with commas.
    /// </summary>
    private static GenericLogEntry FlattenElement(JsonElement el, string prefix = "")
    {
        var entry = new GenericLogEntry();
        FlattenInto(el, prefix, entry.RawFields);
        return entry;
    }

    private static IEnumerable<string> TokenizeQuery(string query)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        foreach (var ch in query)
        {
            if (quote == '\0' && (ch == '"' || ch == '\''))
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (quote != '\0' && ch == quote)
            {
                quote = '\0';
                current.Append(ch);
                continue;
            }

            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static string NormalizeQueryValue(string value)
        => value.Trim().Trim('"', '\'');

    private static HashSet<int> AddNaturalFieldFilters(
        IReadOnlyList<string> tokens,
        HashSet<string> allFields,
        Dictionary<string, string> filters)
    {
        var consumed = new HashSet<int>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var field = ResolveFieldName(NormalizeQueryValue(tokens[i]), allFields);
            if (field is null) continue;

            var valueIndex = -1;
            if (i + 2 < tokens.Count && IsConnector(tokens[i + 1]))
                valueIndex = i + 2;
            else if (i + 1 < tokens.Count && !LooksLikeCommandWord(tokens[i + 1]))
                valueIndex = i + 1;

            if (valueIndex == -1) continue;

            var value = NormalizeQueryValue(tokens[valueIndex]);
            if (string.IsNullOrWhiteSpace(value) || IsConnector(value) || LooksLikeCommandWord(value))
                continue;

            filters[field] = value;
            consumed.Add(i);
            if (valueIndex == i + 2) consumed.Add(i + 1);
            consumed.Add(valueIndex);
        }

        return consumed;
    }

    private static string? ResolveFieldName(string rawField, HashSet<string> allFields)
    {
        var field = rawField.Trim().Trim('"', '\'').ToLowerInvariant();
        if (field.Length == 0) return null;

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["currencu"] = "currency",
            ["curreny"] = "currency",
            ["curr"] = "currency",
            ["ccy"] = "currency",
            ["amoutn"] = "amount",
            ["amt"] = "amount",
            ["ref"] = "payment_ref",
            ["traceid"] = "trace.traceid",
            ["trace_id"] = "trace.traceid",
            ["pod"] = "kubernetes.pod_name",
            ["namespace"] = "kubernetes.namespace_name",
            ["service"] = "kubernetes.labels.app"
        };

        if (aliases.TryGetValue(field, out var alias) && allFields.Contains(alias))
            return alias;

        if (allFields.Contains(field))
            return field;

        var normalized = field.Replace("_", "").Replace(".", "").Replace("-", "");
        var match = allFields.FirstOrDefault(f =>
            f.Replace("_", "").Replace(".", "").Replace("-", "").Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;

        match = allFields.FirstOrDefault(f => LevenshteinDistance(
            f.Replace("_", "").Replace(".", "").Replace("-", ""),
            normalized) <= 1);

        return match;
    }

    private static bool IsConnector(string token)
    {
        var t = NormalizeQueryValue(token).ToLowerInvariant();
        return t is "is" or "are" or "equals" or "equal" or "eq" or ":" or "=";
    }

    private static bool LooksLikeCommandWord(string token)
    {
        var t = NormalizeQueryValue(token).ToLowerInvariant();
        return t is "show" or "all" or "log" or "logs" or "data" or "where"
            or "find" or "search" or "list" or "display" or "for" or "the"
            or "and" or "with" or "having" or "please" or "give" or "me";
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToLowerInvariant(left[i - 1]) == char.ToLowerInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static void FlattenInto(JsonElement el, string prefix, Dictionary<string, string> dict)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in el.EnumerateObject())
        {
            var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            key = key.ToLowerInvariant();

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    dict[key] = prop.Value.GetString() ?? "";
                    break;
                case JsonValueKind.Number:
                    dict[key] = prop.Value.ToString();
                    break;
                case JsonValueKind.True:
                    dict[key] = "true";
                    break;
                case JsonValueKind.False:
                    dict[key] = "false";
                    break;
                case JsonValueKind.Null:
                    dict[key] = "null";
                    break;
                case JsonValueKind.Array:
                    // Join array values as comma-separated string
                    var items = prop.Value.EnumerateArray()
                        .Select(i => i.ValueKind == JsonValueKind.String
                            ? i.GetString()
                            : i.ToString())
                        .Where(s => s != null);
                    dict[key] = string.Join(", ", items);
                    break;
                case JsonValueKind.Object:
                    // Recurse with dot-notation prefix
                    FlattenInto(prop.Value, key, dict);
                    break;
            }
        }
    }

    /// <summary>
    /// Auto-discover schema from entries:
    /// all field names, which ones are *_ref, and sample ref values.
    /// </summary>
    private static DomainSchema BuildSchema(string fileName, List<GenericLogEntry> entries)
    {
        var allFields = entries
            .SelectMany(e => e.RawFields.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        var refFields = allFields
            .Where(f => f.EndsWith("_ref", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sampleRefs = entries
            .SelectMany(e => e.AllRefs)
            .Distinct()
            .Take(5)
            .ToList();

        var index = entries.FirstOrDefault(e => e.Index != null)?.Index;

        return new DomainSchema
        {
            FileName   = fileName,
            Index      = index,
            Fields     = allFields,
            RefFields  = refFields,
            SampleRefs = sampleRefs,
            EntryCount = entries.Count
        };
    }
}
