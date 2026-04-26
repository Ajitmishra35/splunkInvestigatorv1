# Swapping Qdrant for Azure AI Search

The vector store is behind `IVectorStoreService`. Replacing Qdrant with Azure AI Search
requires changes in exactly **3 places** — no business logic changes needed.

## Step 1 — Create the new implementation

Create `Services/AzureAISearchVectorStoreService.cs` implementing `IVectorStoreService`:

```csharp
public sealed class AzureAISearchVectorStoreService : IVectorStoreService
{
    // Implement all 8 interface members using Azure.Search.Documents SDK
    // Collection name pattern: index name = "{prefix}-{domain}"
    // Vector field: float[256], cosine similarity
}
```

Azure AI Search NuGet:
```xml
<PackageReference Include="Azure.Search.Documents" Version="11.*" />
```

## Step 2 — Swap the registration in Program.cs

```csharp
// Remove:
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorStoreService>();

// Add:
builder.Services.AddSingleton<IVectorStoreService, AzureAISearchVectorStoreService>();
```

## Step 3 — Add Azure AI Search config to appsettings.json

```json
"AzureAISearch": {
  "Endpoint": "https://<your-search>.search.windows.net",
  "ApiKey": "<your-admin-key>",
  "IndexPrefix": "splunk"
}
```

Remove (or keep for reference) the `Qdrant` section.

---

No other code changes needed. `EmbeddingService`, `SplunkTools`, `LogFileService`,
`AgentService`, and `Investigator.razor` are all unaffected.
