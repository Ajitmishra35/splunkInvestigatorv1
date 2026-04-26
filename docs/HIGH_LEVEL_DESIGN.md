# High-Level Design

## 1. Overview

Splunk Investigator Agent is a Blazor Server application that helps operations teams investigate Splunk JSON exports using structured search, natural-language search, and AI-assisted analysis.

The application has five main layers:

1. Web UI
2. Agent orchestration
3. Tool layer
4. Log parsing and search
5. Vector search and AI services

## 2. High-Level Architecture

```mermaid
flowchart TB
    User[Ops User] --> UI[Blazor Server UI]
    UI --> DisplaySearch[Local Display Search]
    UI --> Agent[AgentService]

    Agent --> AOAI[Azure OpenAI Chat Deployment]
    Agent --> Tools[SplunkTools]

    Tools --> LogService[LogFileService]
    DisplaySearch --> LogService

    LogService --> DiskLogs[SampleLogs JSON Files]
    LogService --> Uploads[Uploaded JSON Files]
    LogService --> Parser[Generic JSON Parser and Flattener]
    Parser --> RawFields[GenericLogEntry RawFields]

    LogService --> VectorStore{Vector Store Provider}
    VectorStore --> Memory[InMemory]
    VectorStore --> Qdrant[Qdrant]
    VectorStore --> Search[Azure AI Search]

    LogService --> Embeddings[EmbeddingService]
    Embeddings --> AOAIEmbed[Azure OpenAI Embeddings]

    UI --> Grid[Matched Log Grid]
    Agent --> Report[Investigation Report]
    Grid --> User
    Report --> User
```

## 3. Container and Cloud Architecture

```mermaid
flowchart LR
    Dev[Developer Workstation] --> ACRBuild[az acr build]
    ACRBuild --> ACR[Azure Container Registry]
    ACR --> ACA[Azure Container App]
    ACA --> Env[Container App Environment]
    ACA --> Logs[Log Analytics Workspace]
    ACA --> AOAI[Azure OpenAI]
    ACA --> Qdrant[Optional Qdrant]
    ACA --> AISearch[Optional Azure AI Search]
    User[Ops User Browser] --> ACA
```

## 4. Logical Components

| Component | Responsibility |
|---|---|
| `Investigator.razor` | Chat UI, upload UI, matched log grid |
| `AgentService` | AI orchestration and streaming |
| `SplunkTools` | AI-callable investigation tools |
| `LogFileService` | JSON parsing, schema discovery, search, indexing |
| `GenericLogEntry` | Generic flattened log model |
| `EmbeddingService` | Builds embeddings for logs and user queries |
| `IVectorStoreService` | Vector store abstraction |
| `InMemoryVectorStoreService` | Default vector store |
| `QdrantVectorStoreService` | Optional Qdrant integration |
| `AzureAISearchVectorStoreService` | Optional Azure AI Search integration |

## 5. Data Model

Every log record is converted into:

```text
GenericLogEntry
  RawFields: Dictionary<string, string>
```

Nested fields are flattened:

```text
kubernetes.pod_name
kubernetes.labels.app
http.statuscode
trace.traceid
context.requestid
```

Reference fields are auto-detected by suffix:

```text
payment_ref
transfer_ref
loan_ref
*_ref
```

## 6. Search Behavior

The search system supports:

- Exact field search: `field=value`
- Natural field search: `where field is value`
- Free-text search across all field names and values
- Numeric value normalization
- Boolean value normalization
- Quoted values with spaces
- Light field typo handling

## 7. Security Design

Sensitive data is protected in AI and vector payloads by excluding high-risk identifier fields.

Searchable but sensitive-to-mask:

- Account numbers
- IBAN
- Card data
- User identifiers
- Contact data
- Passwords and credentials

Operational fields intentionally visible:

- Amount
- Currency
- Status
- Event
- Error code
- Service names
- Pod names
- Trace IDs

## 8. Availability and Fallback

If Qdrant or Azure AI Search is selected but unavailable:

1. Startup continues.
2. App logs a warning.
3. InMemory vector search is used.
4. Exact local search continues to work.

This keeps the ops workflow available even when optional vector infrastructure is down.
