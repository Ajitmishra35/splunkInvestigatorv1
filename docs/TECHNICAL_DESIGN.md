# Technical Design: Splunk Investigator Agent

## 1. Purpose

Splunk Investigator Agent is an operations-focused investigation assistant for exported Splunk JSON logs. It lets an ops user search loaded logs by natural language, reference numbers, exact field filters, and common incident phrases. The agent summarizes evidence, timelines, issues, likely root cause, and next checks while keeping sensitive identifiers masked.

The app is intentionally domain-generic. It does not require hardcoded payment, transfer, or loan models. Every JSON field is flattened into a searchable key-value dictionary.

## 2. Scope

In scope:

- Load sample Splunk JSON exports from `SampleLogs/`.
- Upload additional JSON exports from the browser.
- Parse JSON arrays, single JSON objects, wrapper objects, and NDJSON.
- Flatten nested JSON fields using dot notation.
- Search exact fields such as `index=payments`, `currency=GBP`, `amount=1200`, `http.statusCode=202`, and `trace.traceId=...`.
- Handle common natural-language field filters such as `where currency is GBP`.
- Handle simple field typos such as `currencu` -> `currency` and `amoutn` -> `amount`.
- Generate AI investigation responses through Azure OpenAI and Microsoft.Extensions.AI tool calling.
- Use vector search through InMemory, Qdrant, or Azure AI Search.

Out of scope for the current prototype:

- Direct live Splunk API integration.
- Multi-user authentication and authorization.
- Persistent user conversation history.
- Production-grade audit logging.
- Role-based field-level masking.

## 3. Technology Stack

| Layer | Technology |
|---|---|
| UI | Blazor Server |
| Runtime | .NET 10 |
| AI abstraction | Microsoft.Extensions.AI |
| LLM provider | Azure OpenAI |
| Embeddings | Azure OpenAI `text-embedding-3-small` |
| Vector store options | InMemory, Qdrant, Azure AI Search |
| Container runtime | Docker |
| Cloud hosting | Azure Container Apps |
| Registry | Azure Container Registry |

## 4. Application Components

### 4.1 UI Layer

File: `Components/Pages/Investigator.razor`

Responsibilities:

- Render chat interface.
- Upload JSON log files.
- Show log status and quick queries.
- Run a local display search before invoking the AI agent.
- Render matched rows in a grid.
- Stream AI response chunks to the browser.

### 4.2 Agent Layer

File: `Services/AgentService.cs`

Responsibilities:

- Defines the system prompt for the investigation agent.
- Sends conversation context to Azure OpenAI.
- Registers tools from `SplunkTools`.
- Streams assistant output back to the UI.

Important behavior:

- The agent must ground answers in loaded JSON logs and tool results.
- The agent should call schema discovery before searching.
- The agent should use exact filters for structured questions.
- Sensitive identifiers are not exposed in AI summaries.

### 4.3 Tool Layer

File: `Tools/SplunkTools.cs`

Registered AI tools:

- `DiscoverSchema`
- `SearchByRef`
- `RunQuery`
- `GetStatistics`
- `GetErrors`
- `GetTimeline`
- `GetSampleEntries`
- `GetTimeOrderedEntries`
- `GetDomainSummary`
- `SearchSemantic`
- `SearchExact`
- `SearchHybrid`

The tool layer serializes matched log rows after masking sensitive fields.

### 4.4 Log Parsing and Search Layer

File: `Services/LogFileService.cs`

Responsibilities:

- Read sample logs from disk.
- Parse uploaded logs.
- Flatten JSON into `GenericLogEntry.RawFields`.
- Discover schema per loaded file.
- Provide exact and natural search over all loaded fields.
- Index records into the configured vector store.

Supported search examples:

```text
index=payments amount=1200
currency=GBP
show data where currency is GBP
show data where currencu is GBP
http.statusCode=202
kubernetes.labels.app=payment-risk-service
trace.traceId=demo-trace-pay-002
level=ERROR
status=BLOCKED
gateway timeout
```

### 4.5 Domain Model

File: `Models/Models.cs`

Key classes:

- `GenericLogEntry`
- `DomainSchema`
- `UploadedLogFile`
- `ChatMessage`

`GenericLogEntry.RawFields` is the source of truth. Convenience fields like `Time`, `Index`, `Level`, `Event`, `Status`, and `Message` are read from `RawFields`.

Matching behavior:

- Case-insensitive string comparison.
- Numeric normalization, so `1200`, `1200.0`, and `1200.00` match.
- Boolean normalization, so `true` and `TRUE` match.
- Quoted values are normalized.

### 4.6 Vector Store Layer

Files:

- `Services/IVectorStoreService.cs`
- `Services/InMemoryVectorStoreService.cs`
- `Services/QdrantVectorStoreService.cs`
- `Services/AzureAISearchVectorStoreService.cs`

Provider selection is controlled by:

```json
{
  "VectorStore": {
    "Provider": "InMemory"
  }
}
```

Supported provider values:

- `InMemory`
- `Qdrant`
- `AzureAISearch`

If Qdrant or Azure AI Search is configured but unavailable, the app falls back to in-memory vector search.

## 5. Data Flow

1. App starts.
2. `LogFileService.InitializeSampleLogsAsync()` reads JSON files from `SampleLogs/`.
3. JSON records are flattened into `RawFields`.
4. Schema metadata is generated per file.
5. Logs are indexed into the selected vector store.
6. User asks a question.
7. UI runs `SearchForDisplayAsync()` to populate the grid.
8. Agent receives the user query plus matched row preview.
9. Agent calls tools as needed.
10. Tool results are serialized with sensitive identifiers masked.
11. Agent returns a grounded investigation report.
12. UI renders the response and matched log grid.

## 6. Search Design

### Exact field filters

Exact filters use `key=value` format and work against any flattened field:

```text
index=payments level=ERROR
currency=GBP
amount=1200
http.statusCode=202
context.app="Banking Platform - Payment Services"
```

### Natural field filters

The parser converts common ops phrases into exact filters:

```text
show data where currency is GBP
```

becomes:

```text
currency=GBP
```

### Typo handling

The parser includes aliases and light fuzzy matching for common field-name typos:

| User field | Resolved field |
|---|---|
| `currencu` | `currency` |
| `curreny` | `currency` |
| `ccy` | `currency` |
| `amoutn` | `amount` |
| `amt` | `amount` |
| `traceid` | `trace.traceid` |
| `pod` | `kubernetes.pod_name` |
| `namespace` | `kubernetes.namespace_name` |
| `service` | `kubernetes.labels.app` |

## 7. Security and Data Handling

The app is designed for sanitized Splunk exports in this prototype.

Sensitive fields masked from AI output and external vector payloads include:

- User IDs
- Account numbers
- IBANs
- Sort codes
- Card numbers
- PAN/CVV
- SSN/national ID
- Email
- Phone
- Passwords
- IP address fields listed as sensitive

Operational fields such as `amount`, `currency`, `status`, `event`, and `error_code` remain searchable and visible because ops teams need them for investigation.

## 8. Configuration

Primary config file: `appsettings.json`

Important sections:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
    "ApiKey": "YOUR-KEY",
    "DeploymentName": "gpt-5.4-mini",
    "EmbeddingDeploymentName": "text-embedding-3-small",
    "EmbeddingDimensions": 256
  },
  "VectorStore": {
    "Provider": "InMemory"
  },
  "SplunkSettings": {
    "LogsFolder": "SampleLogs",
    "WebUrl": "https://splunk.yourcompany.com:8000",
    "DefaultIndex": "payments"
  }
}
```

For Azure deployment, sensitive values are supplied as Container App environment variables and secrets.

## 9. Deployment

Current deployment target:

- Azure Container App: `rg-ajitmishra-0084-splunkagent`
- Resource group: `rg-ajitmishra-0084`
- ACR: `ca15ef92c185acr.azurecr.io`
- Port: `8080`

Build and deploy pattern:

```powershell
az acr build `
  --registry ca15ef92c185acr `
  --image splunkinvestigator:<tag> `
  --image splunkinvestigator:latest .

az containerapp update `
  -g rg-ajitmishra-0084 `
  -n rg-ajitmishra-0084-splunkagent `
  --image ca15ef92c185acr.azurecr.io/splunkinvestigator:<tag> `
  --set-env-vars DEPLOY_STAMP=<timestamp>
```

## 10. Operational Test Queries

Use these after deployment:

```text
show data where currency is GBP
show data where currencu is GBP
currency=GBP
index=payments amount=1200
index=payments amount=1200.00
show me gateway related issue
gateway timeout
event=GATEWAY_TIMEOUT
status=BLOCKED
level=ERROR
trace.traceId=demo-trace-pay-002
```

Expected examples from current sample logs:

- `currency=GBP` returns 5 rows.
- `index=payments amount=1200` returns payment `PAY-20260426-002`.
- `level=ERROR` returns 3 rows.
- `gateway timeout` returns the gateway timeout investigation for `PAY-20260426-001`.

## 11. Known Limitations

- Natural-language typo handling is intentionally light and field-focused, not a full spell-check engine.
- Uploaded logs are held in application memory.
- InMemory vector store resets on app restart.
- Azure AI Search and Qdrant providers require correct external service configuration.
- Current Splunk integration is export-file based, not direct live search.

## 12. Future Enhancements

- Add direct Splunk REST API search connector.
- Add authentication and RBAC.
- Add downloadable investigation reports.
- Add automated regression tests for search parser behavior.
- Add field-level masking policy configuration.
- Add CI/CD workflow for build and Azure Container App deployment.
