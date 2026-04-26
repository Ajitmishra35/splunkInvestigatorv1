using Microsoft.Extensions.AI;
using SplunkInvestigator.Models;
using SplunkInvestigator.Services;

namespace SplunkInvestigator.Services;

/// <summary>
/// Generic Splunk Investigator Agent.
/// Works with ANY domain — it discovers schema first, then queries.
/// </summary>
public class AgentService
{
    private readonly IChatClient _chatClient;
    private readonly Tools.SplunkTools _tools;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentService> _logger;

    private const string SystemPrompt = """
        You are a Splunk Investigator Agent for a banking platform running on Kubernetes.
        Multiple domains available: payments, transfers, loans, cards, fraud — and any future domain.

        ## Investigation Workflow — ALWAYS follow this order:

        1. DiscoverSchema
           → See what files/domains/fields are loaded

        2. GetDomainSummary(domain)
           → Get overview before diving in

        3. Choose search method:
           - Exact ref (TXN-*, TRF-*, LOAN-*)  → SearchExact
           - Natural language query             → SearchSemantic
           - Combined                           → SearchHybrid
           - Timeline of one ref                → GetTimeline

        4. Build investigation report

        ## Report rules:
        - Neutral tone — factual only
        - NO sensitive data: amounts, account numbers, IBANs, card numbers, user IDs
        - NO recommendations unless asked
        - End with Splunk URL reference
        - Sections: Overview → Timeline → Issues → Splunk URL

        ## If vector store unavailable:
        Fall back to existing in-memory search tools (SearchByRef, RunQuery, GetErrors).
        Tell user: 'Using local search - vector store unavailable'
        """;

    public AgentService(
        IChatClient chatClient,
        Tools.SplunkTools tools,
        IConfiguration config,
        ILogger<AgentService> logger)
    {
        _chatClient = chatClient;
        _tools = tools;
        _config = config;
        _logger = logger;
    }

    public async IAsyncEnumerable<string> InvestigateStreamAsync(
        string userQuery,
        List<SplunkInvestigator.Models.ChatMessage> conversationHistory)
    {
        var splunkWebUrl = _config["SplunkSettings:WebUrl"] ?? "https://splunk.yourcompany.com:8000";

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, SystemPrompt + $"\n\nSplunk Web URL: {splunkWebUrl}")
        };

        foreach (var h in conversationHistory.TakeLast(10))
        {
            var role = h.Role == "user" ? ChatRole.User : ChatRole.Assistant;
            messages.Add(new(role, h.Content));
        }

        messages.Add(new(ChatRole.User, userQuery));

        var options = new ChatOptions
        {
            Tools = [.. _tools.GetTools()],
            ToolMode = ChatToolMode.Auto,
            Temperature = 0.2f
        };

        await foreach (var chunk in _chatClient.GetStreamingResponseAsync(messages, options))
        {
            if (!string.IsNullOrEmpty(chunk.Text))
                yield return chunk.Text;
        }
    }
}
