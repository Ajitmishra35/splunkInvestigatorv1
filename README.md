# Splunk Investigator Agent
### .NET 10 · Blazor Server · Microsoft.Extensions.AI · Azure OpenAI

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Blazor Server (.NET 10)                   │
│                                                             │
│  Investigator.razor  ──► AgentService                       │
│       (UI/Chat)              │                              │
│                              │  Microsoft.Extensions.AI     │
│                              │  IChatClient (MEAI)          │
│                              │  .UseFunctionInvocation()    │
│                              ▼                              │
│                        Azure OpenAI                         │
│                        (GPT-4o via your endpoint)           │
│                              │                              │
│                    Tool-calling loop                        │
│                              │                              │
│                    ┌─────────▼──────────┐                   │
│                    │   SplunkTools       │                   │
│                    │  (AIFunction tools) │                   │
│                    └─────────┬──────────┘                   │
│                              │                              │
│                    ┌─────────▼──────────┐                   │
│                    │   LogFileService    │                   │
│                    │  reads exported     │                   │
│                    │  Splunk JSON files  │                   │
│                    │  from /SampleLogs   │                   │
│                    └────────────────────┘                   │
└─────────────────────────────────────────────────────────────┘
```

## Key Design Decisions

| Concern | Decision |
|---|---|
| **AI Abstraction** | `Microsoft.Extensions.AI` (MEAI) — the new unified abstraction from 2025 |
| **LLM Provider** | Azure OpenAI via `Azure.AI.OpenAI` SDK → wrapped as `IChatClient` |
| **Tool Calling** | `AIFunctionFactory.Create()` + `.UseFunctionInvocation()` middleware — automatic agentic loop |
| **No MCP** | Tools read local exported Splunk JSON files directly — simple, zero-infra |
| **UI** | Blazor Server with streaming SSE, real-time updates |
| **Security** | Sensitive fields (card data, raw user IDs, amounts) stripped before AI context |

## Setup

### 1. Configure Azure OpenAI in appsettings.json
```json
{
  "AzureOpenAI": {
    "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
    "ApiKey": "YOUR-KEY",
    "DeploymentName": "gpt-4o"
  }
}
```

### 2. Add Your Splunk Exports
Drop any exported Splunk JSON files into the `SampleLogs/` folder.

**Export format** (standard Splunk JSON export):
```json
[
  {
    "_time": "2026-04-25T08:01:12Z",
    "index": "payments",
    "level": "ERROR",
    "transaction_ref": "TXN-12345",
    "event": "GATEWAY_ERROR",
    "status": "FAILED",
    "message": "Gateway timeout after 2000ms"
  }
]
```

> You can export directly from Splunk: **Search → Export → JSON**

### 3. Run
```bash
dotnet run --project SplunkInvestigator.csproj
```
Navigate to `http://localhost:5005`

## Agent Tools

| Tool | Description |
|---|---|
| `SearchByTransactionRef` | Find all events for a transaction ref number |
| `RunSplQuery` | SPL-style query: `index=payments level=ERROR` |
| `GetLogStatistics` | Total events, error counts, transaction counts |
| `GetErrorEvents` | All ERROR level events |
| `GetTransactionTimeline` | Chronological timeline for a transaction |

## Adding New Tools
1. Add a private method to `SplunkTools.cs`
2. Decorate with `[Description("...")]`
3. Add `yield return AIFunctionFactory.Create(YourMethod);` in `GetTools()`

That's it — the agent picks it up automatically.

## Adding New Log Fields
Edit `Models/Models.cs` → `SplunkLogEntry` to add new JSON fields.
Edit `LogFileService.cs` filter logic if you want new SPL filter keys.
