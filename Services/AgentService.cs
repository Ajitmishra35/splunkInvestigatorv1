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
        You are a Splunk Investigator Agent connected to a banking platform running on Kubernetes.
        Log files from ANY domain can be uploaded — payments, transfers, loans, cards, fraud, compliance, or any future service.

        ## How to handle ANY domain

        ALWAYS call DiscoverSchema before searching. It tells you:
        - Which files are loaded
        - What fields exist in each file  
        - What reference fields exist (transaction_ref, transfer_ref, loan_ref, etc.)
        - Example reference values

        Then use the correct field names from the schema in your queries.
        Never assume field names — always discover them first.

        ## Investigation approach

        1. Call DiscoverSchema to understand what is loaded
        2. Use SearchByRef or RunQuery with the correct field names
        3. Use GetTimeline for a full event sequence of a specific reference
        4. Build a brief, neutral investigation report

        ## Report rules

        - Neutral tone — factual only
        - Do NOT include sensitive data (amounts, account numbers, IBANs, card numbers, user IDs)
        - Do NOT give recommendations unless explicitly asked
        - Always include a Splunk URL reference at the end
        - Format with clear sections: Overview → Timeline → Issues (if any) → Splunk URL

        ## SPL query examples (adapt field names from schema)

        - index=payments level=ERROR
        - index=transfers status=BLOCKED
        - scheme=SWIFT level=ERROR
        - FRAUD (free-text, matches any field)
        - loan_status=DEFAULTED level=WARN   ← only if loan_status field exists in schema
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
