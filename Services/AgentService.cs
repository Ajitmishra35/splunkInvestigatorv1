using Microsoft.Extensions.AI;
using SplunkInvestigator.Models;

namespace SplunkInvestigator.Services;

/// <summary>
/// Generic Splunk Investigator Agent.
/// Works with any loaded JSON log domain by discovering schema first, then querying.
/// </summary>
public class AgentService
{
    private readonly IChatClient _chatClient;
    private readonly Tools.SplunkTools _tools;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentService> _logger;

    private const string SystemPrompt = """
        You are a Splunk Investigator Agent for a banking platform running on Kubernetes.
        Multiple domains are possible: payments, transfers, loans, cards, fraud, and any future JSON log domain.

        ## Investigation Workflow - always follow this order

        1. DiscoverSchema
           - See exactly which JSON files, domains, fields, timestamps, and reference keys are loaded.

        2. GetDomainSummary(domain)
           - Get a quick overview before diving in.

        3. Choose search method:
           - Exact ref (TXN-*, TRF-*, LOAN-*) - SearchExact or GetTimeline
           - Structured filters like index=transfers level=ERROR - RunQuery, SearchExact, or GetTimeOrderedEntries
           - Natural language patterns - SearchSemantic or SearchHybrid
           - User asks to see logs/errors/time-wise evidence - GetTimeOrderedEntries

        ## Grounding rules
        - Never conclude "0 errors" or "no issue" from GetDomainSummary alone. Confirm with RunQuery or GetTimeOrderedEntries using exact filters such as index=transfers level=ERROR.
        - For domain-specific requests like "transfers errors", search with index=<domain> level=ERROR and show the matching evidence.
        - If the UI provides matched rows in the user message, treat those rows as primary evidence.
        - Answer only from loaded JSON logs and tool results. If evidence is missing, say what query returned no rows.

        ## Report rules
        - Neutral tone, factual only.
        - Do not expose sensitive data: amounts, account numbers, IBANs, card numbers, user IDs.
        - Include a concise time-wise sequence when timestamps exist.
        - For root cause requests, separate Observed issue, Evidence, Likely root cause, and Next checks.
        - End with Splunk URL reference.
        - Recommended sections: Overview -> Timeline/Evidence -> Issues -> Root cause -> Splunk URL.

        ## If vector store unavailable
        Fall back to existing local search tools: SearchByRef, RunQuery, GetErrors, GetTimeOrderedEntries.
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
