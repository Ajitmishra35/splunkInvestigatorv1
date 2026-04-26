# Sequence Diagrams

## 1. Startup and Sample Log Indexing

```mermaid
sequenceDiagram
    autonumber
    participant Host as App Host
    participant Program as Program.cs
    participant LogService as LogFileService
    participant Parser as JSON Parser
    participant Embedding as EmbeddingService
    participant Vector as IVectorStoreService
    participant AOAI as Azure OpenAI Embeddings

    Host->>Program: Start application
    Program->>LogService: InitializeSampleLogsAsync()
    LogService->>LogService: Find SampleLogs/*.json
    loop Each JSON file
        LogService->>Parser: ParseGeneric(content)
        Parser-->>LogService: GenericLogEntry list
        LogService->>LogService: BuildSchema(file, entries)
        LogService->>Vector: InitializeCollectionAsync(domain)
        LogService->>Embedding: EmbedBatchAsync(entries)
        Embedding->>AOAI: Generate embeddings
        AOAI-->>Embedding: Embedding vectors
        Embedding-->>LogService: Vectors
        LogService->>Vector: UpsertBatchAsync(domain, entries, vectors)
    end
    LogService-->>Program: Sample logs ready
```

## 2. Natural Search With Matched Grid

```mermaid
sequenceDiagram
    autonumber
    participant User as Ops User
    participant UI as Investigator.razor
    participant LogService as LogFileService
    participant Agent as AgentService
    participant Tools as SplunkTools
    participant AOAI as Azure OpenAI Chat

    User->>UI: show data where currencu is GBP
    UI->>LogService: SearchForDisplayAsync(query)
    LogService->>LogService: Tokenize query
    LogService->>LogService: Resolve currencu -> currency
    LogService->>LogService: Apply currency=GBP
    LogService-->>UI: 5 matched rows
    UI->>Agent: InvestigateStreamAsync(query + row preview)
    Agent->>AOAI: User query, system prompt, tools
    AOAI->>Tools: DiscoverSchema()
    Tools->>LogService: GetSchemaDescription()
    LogService-->>Tools: Loaded files and fields
    Tools-->>AOAI: Schema
    AOAI->>Tools: GetTimeOrderedEntries("currency=GBP")
    Tools->>LogService: SearchBySplQueryAsync("currency=GBP")
    LogService-->>Tools: 5 rows
    Tools-->>AOAI: Masked rows
    AOAI-->>Agent: Investigation summary stream
    Agent-->>UI: Response chunks
    UI-->>User: Summary plus matched log grid
```

## 3. Exact Field Search

```mermaid
sequenceDiagram
    autonumber
    participant User as Ops User
    participant UI as Investigator.razor
    participant LogService as LogFileService
    participant Entry as GenericLogEntry
    participant Agent as AgentService

    User->>UI: index=payments amount=1200
    UI->>LogService: SearchForDisplayAsync(query)
    LogService->>LogService: Parse key/value filters
    LogService->>Entry: Matches("index", "payments")
    Entry-->>LogService: true
    LogService->>Entry: Matches("amount", "1200")
    Entry->>Entry: Numeric compare 1200.00 == 1200
    Entry-->>LogService: true
    LogService-->>UI: PAY-20260426-002 row
    UI->>Agent: Send query with matched row preview
    Agent-->>UI: AI analysis grounded in the matched row
    UI-->>User: Payment fraud/block event
```

## 4. Gateway Issue Investigation

```mermaid
sequenceDiagram
    autonumber
    participant User as Ops User
    participant UI as Investigator.razor
    participant Agent as AgentService
    participant AOAI as Azure OpenAI Chat
    participant Tools as SplunkTools
    participant LogService as LogFileService

    User->>UI: show me gateway related issue
    UI->>LogService: SearchForDisplayAsync(query)
    LogService-->>UI: Gateway-related payment rows
    UI->>Agent: Query plus matched row preview
    Agent->>AOAI: Prompt and tools
    AOAI->>Tools: DiscoverSchema()
    Tools-->>AOAI: Schema
    AOAI->>Tools: GetTimeOrderedEntries("gateway timeout")
    Tools->>LogService: SearchBySplQueryAsync("gateway timeout")
    LogService-->>Tools: Authorization, timeout, retry, completion rows
    Tools-->>AOAI: Evidence
    AOAI-->>Agent: Gateway issue summary
    Agent-->>UI: Streaming response
    UI-->>User: Timeline and likely root cause
```

## 5. Upload Log File

```mermaid
sequenceDiagram
    autonumber
    participant User as Ops User
    participant UI as Investigator.razor
    participant LogService as LogFileService
    participant Parser as JSON Parser
    participant Embedding as EmbeddingService
    participant Vector as IVectorStoreService

    User->>UI: Upload JSON export
    UI->>LogService: AddUploadedLogsAsync(file, stream)
    LogService->>Parser: ParseGeneric(content)
    Parser-->>LogService: Entries
    LogService->>LogService: BuildSchema(file, entries)
    LogService->>Vector: InitializeCollectionAsync(domain)
    LogService->>Embedding: EmbedBatchAsync(entries)
    Embedding-->>LogService: Vectors
    LogService->>Vector: UpsertBatchAsync(domain, entries, vectors)
    LogService-->>UI: UploadedLogFile result
    UI-->>User: File loaded and searchable
```

## 6. Azure Deployment Flow

```mermaid
sequenceDiagram
    autonumber
    participant Dev as Developer
    participant CLI as Azure CLI
    participant ACR as Azure Container Registry
    participant ACA as Azure Container App
    participant Browser as User Browser

    Dev->>CLI: az acr build --image splunkinvestigator:<tag>
    CLI->>ACR: Upload source archive
    ACR->>ACR: Build Docker image
    ACR->>ACR: Push <tag> and latest
    Dev->>CLI: az containerapp update --image <tag>
    CLI->>ACA: Create new revision
    ACA->>ACR: Pull image
    ACA->>ACA: Start container on port 8080
    ACA-->>CLI: latestReadyRevision updated
    Browser->>ACA: HTTPS request
    ACA-->>Browser: 200 OK
```
