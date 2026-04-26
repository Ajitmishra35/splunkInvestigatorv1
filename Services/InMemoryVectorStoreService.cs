using SplunkInvestigator.Models;

namespace SplunkInvestigator.Services;

public sealed class InMemoryVectorStoreService : IVectorStoreService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<StoredVector>> _domains = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => true;

    public Task InitializeCollectionAsync(string domain)
    {
        lock (_sync)
        {
            _domains.TryAdd(NormalizeDomain(domain), []);
        }

        return Task.CompletedTask;
    }

    public Task UpsertBatchAsync(string domain, List<GenericLogEntry> entries, List<float[]> vectors)
    {
        var key = NormalizeDomain(domain);

        lock (_sync)
        {
            if (!_domains.TryGetValue(key, out var stored))
            {
                stored = [];
                _domains[key] = stored;
            }

            for (var i = 0; i < entries.Count && i < vectors.Count; i++)
                stored.Add(new StoredVector(key, entries[i], vectors[i]));
        }

        return Task.CompletedTask;
    }

    public Task<List<GenericLogEntry>> SearchByRefAsync(string refValue, string? domain = null)
    {
        var results = Snapshot(domain)
            .Where(v => v.Entry.AllRefs.Any(r => r.Equals(refValue, StringComparison.OrdinalIgnoreCase)))
            .Select(v => v.Entry)
            .OrderBy(e => e.Time)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<List<GenericLogEntry>> SearchByFiltersAsync(Dictionary<string, string> filters, string? domain = null)
    {
        var results = Snapshot(domain)
            .Where(v => MatchesFilters(v.Entry, filters))
            .Select(v => v.Entry)
            .OrderBy(e => e.Time)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<List<GenericLogEntry>> SearchSemanticAsync(float[] queryVector, string? domain = null, int topK = 10)
    {
        var results = Snapshot(domain)
            .Select(v => new { v.Entry, Score = Cosine(queryVector, v.Vector) })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Entry)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<List<GenericLogEntry>> SearchHybridAsync(
        float[] queryVector,
        Dictionary<string, string> filters,
        string? domain = null,
        int topK = 10)
    {
        var results = Snapshot(domain)
            .Where(v => MatchesFilters(v.Entry, filters))
            .Select(v => new { v.Entry, Score = Cosine(queryVector, v.Vector) })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Entry)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<DomainStats> GetDomainStatsAsync(string domain)
    {
        var entries = Snapshot(domain)
            .Select(v => v.Entry)
            .ToList();

        var stats = BuildStats(domain, entries);
        return Task.FromResult(stats);
    }

    public Task<List<string>> GetAllDomainsAsync()
    {
        lock (_sync)
        {
            return Task.FromResult(_domains.Keys.OrderBy(k => k).ToList());
        }
    }

    private List<StoredVector> Snapshot(string? domain = null)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(domain))
                return _domains.TryGetValue(NormalizeDomain(domain), out var entries)
                    ? entries.ToList()
                    : [];

            return _domains.Values.SelectMany(v => v).ToList();
        }
    }

    private static bool MatchesFilters(GenericLogEntry entry, Dictionary<string, string> filters)
    {
        foreach (var (key, value) in filters)
        {
            if (key.Equals("ref_value", StringComparison.OrdinalIgnoreCase))
            {
                if (!entry.AllRefs.Any(r => r.Equals(value, StringComparison.OrdinalIgnoreCase)))
                    return false;

                continue;
            }

            if (!entry.Matches(key, value))
                return false;
        }

        return true;
    }

    private static DomainStats BuildStats(string domain, List<GenericLogEntry> entries)
    {
        var times = entries
            .Select(e => e.Time)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .OrderBy(t => t)
            .ToList();

        var topEvents = entries
            .Select(e => e.Event)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key!)
            .ToList();

        return new DomainStats(
            Domain: domain,
            TotalEntries: entries.Count,
            ErrorCount: entries.Count(e => e.Level?.Equals("ERROR", StringComparison.OrdinalIgnoreCase) == true),
            WarningCount: entries.Count(e => e.Level?.Equals("WARN", StringComparison.OrdinalIgnoreCase) == true),
            EarliestTime: times.FirstOrDefault(),
            LatestTime: times.LastOrDefault(),
            TopEvents: topEvents);
    }

    private static float Cosine(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0;

        double dot = 0;
        double magA = 0;
        double magB = 0;

        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0;
        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }

    private static string NormalizeDomain(string domain)
        => domain.Trim().ToLowerInvariant();

    private sealed record StoredVector(string Domain, GenericLogEntry Entry, float[] Vector);
}

