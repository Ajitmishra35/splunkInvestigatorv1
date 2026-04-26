using SplunkInvestigator.Models;

namespace SplunkInvestigator.Services;

public interface IVectorStoreService
{
    bool IsAvailable { get; }

    Task InitializeCollectionAsync(string domain);

    Task UpsertBatchAsync(string domain,
                          List<GenericLogEntry> entries,
                          List<float[]> vectors);

    Task<List<GenericLogEntry>> SearchByRefAsync(
                                    string refValue,
                                    string? domain = null);

    Task<List<GenericLogEntry>> SearchByFiltersAsync(
                                    Dictionary<string, string> filters,
                                    string? domain = null);

    Task<List<GenericLogEntry>> SearchSemanticAsync(
                                    float[] queryVector,
                                    string? domain = null,
                                    int topK = 10);

    Task<List<GenericLogEntry>> SearchHybridAsync(
                                    float[] queryVector,
                                    Dictionary<string, string> filters,
                                    string? domain = null,
                                    int topK = 10);

    Task<DomainStats> GetDomainStatsAsync(string domain);

    Task<List<string>> GetAllDomainsAsync();
}

public record DomainStats(
    string Domain,
    long TotalEntries,
    long ErrorCount,
    long WarningCount,
    string? EarliestTime,
    string? LatestTime,
    List<string> TopEvents
);
