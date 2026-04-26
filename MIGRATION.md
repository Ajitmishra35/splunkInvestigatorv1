# Vector Store Provider Configuration

The app now supports three vector-store modes behind the same `IVectorStoreService`
interface:

- `InMemory` - stores embeddings in app memory. Best for demos, local testing, and
  single-container pilots.
- `Qdrant` - stores embeddings in Qdrant Cloud.
- `AzureAISearch` - stores embeddings in Azure AI Search indexes.

If `Qdrant` or `AzureAISearch` is selected but unavailable at startup, the app falls
back to `InMemory` so existing upload and investigation flows still work.

## appsettings.json

```json
{
  "VectorStore": {
    "Provider": "InMemory"
  },
  "Qdrant": {
    "Endpoint": "https://YOUR-QDRANT-CLUSTER.qdrant.io",
    "ApiKey": "YOUR-QDRANT-KEY",
    "CollectionPrefix": "splunk"
  },
  "AzureAISearch": {
    "Endpoint": "https://YOUR-SEARCH-SERVICE.search.windows.net",
    "ApiKey": "YOUR-AZURE-AI-SEARCH-KEY",
    "IndexPrefix": "splunk"
  }
}
```

Allowed `VectorStore:Provider` values:

```text
InMemory
Qdrant
AzureAISearch
```

Environment variable examples:

```powershell
$env:VectorStore__Provider = "AzureAISearch"
$env:AzureAISearch__Endpoint = "https://my-search.search.windows.net"
$env:AzureAISearch__ApiKey = "<admin-or-query-key-with-write-access>"
$env:AzureAISearch__IndexPrefix = "splunk"
```

## Storage Names

Qdrant collection pattern:

```text
{CollectionPrefix}-{domain}
```

Azure AI Search index pattern:

```text
{IndexPrefix}-{domain}
```

Examples:

```text
splunk-payments
splunk-transfers
splunk-fraud
```

## Azure AI Search Implementation

`Services/AzureAISearchVectorStoreService.cs` implements all members of
`IVectorStoreService` using the `Azure.Search.Documents` SDK.

Index details:

- Key field: `id`
- Vector field: `vector`
- Vector dimensions: `256`
- Similarity metric: cosine
- Algorithm: HNSW
- Metadata fields: `domain`, `time`, `level`, `event`, `status`, `error_code`,
  `ref_value`, `ref_field`, `host`, `pod`, `namespace`, `message`, `full_json`

Sensitive fields are excluded from `full_json` before being persisted to external
vector stores.

## In-Memory Mode

In-memory mode still supports real vector search:

1. Logs are parsed.
2. Azure OpenAI embeddings are generated.
3. Vectors and log entries are stored in RAM.
4. Semantic and hybrid search use cosine similarity.

Limitations:

- Data is lost when the app restarts.
- Data is not shared across multiple container instances.
- Large production datasets should use Qdrant or Azure AI Search.

