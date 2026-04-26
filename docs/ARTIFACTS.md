# Artifacts and Deployment Inventory

## 1. Source Code Artifacts

| Path | Purpose |
|---|---|
| `Program.cs` | Application startup, DI registration, AI/vector provider setup |
| `Components/Pages/Investigator.razor` | Main Blazor chat and investigation UI |
| `Models/Models.cs` | Generic log models and matching behavior |
| `Services/AgentService.cs` | Azure OpenAI agent orchestration |
| `Services/LogFileService.cs` | Log parsing, flattening, schema discovery, exact/natural search |
| `Services/EmbeddingService.cs` | Azure OpenAI embeddings |
| `Services/IVectorStoreService.cs` | Vector store contract |
| `Services/InMemoryVectorStoreService.cs` | In-memory vector search provider |
| `Services/QdrantVectorStoreService.cs` | Qdrant vector search provider |
| `Services/AzureAISearchVectorStoreService.cs` | Azure AI Search vector search provider |
| `Tools/SplunkTools.cs` | AI-callable investigation tools |
| `SampleLogs/*.json` | Sanitized Splunk-style sample logs |
| `Dockerfile` | Container image build definition |
| `appsettings.json` | Local/default configuration placeholders |
| `SplunkInvestigator.csproj` | .NET project file and package dependencies |
| `SplunkInvestigator_v3.sln` | Visual Studio solution |

## 2. Sample Log Artifacts

| File | Index | Current rows | Notes |
|---|---:|---:|---|
| `SampleLogs/payments_export.json` | `payments` | 6 | Payment accepted, gateway timeout, retry, fraud block |
| `SampleLogs/loans_export.json` | `loans` | 4 | Loan application, credit pass, disbursement, rejection |
| `SampleLogs/transfers_export.json` | `transfers` | 8 | Domestic transfer, SWIFT flow, sanctions block |

Expected aggregate counts:

- Total rows: 18
- ERROR rows: 3
- WARN rows: 1
- `currency=GBP` rows: 5

## 3. Runtime Configuration Artifacts

### Azure OpenAI

Environment variables:

```text
AzureOpenAI__Endpoint
AzureOpenAI__ApiKey
AzureOpenAI__DeploymentName
AzureOpenAI__EmbeddingDeploymentName
AzureOpenAI__EmbeddingDimensions
```

### Vector Store

Environment variables:

```text
VectorStore__Provider
Qdrant__Endpoint
Qdrant__ApiKey
Qdrant__CollectionPrefix
AzureAISearch__Endpoint
AzureAISearch__ApiKey
AzureAISearch__IndexPrefix
```

### Application

Environment variables:

```text
ASPNETCORE_ENVIRONMENT
DEPLOY_STAMP
SplunkSettings__LogsFolder
SplunkSettings__WebUrl
SplunkSettings__DefaultIndex
```

## 4. Azure Artifacts

| Artifact | Name |
|---|---|
| Subscription | `Visual Studio Enterprise Subscription` |
| Resource group | `rg-ajitmishra-0084` |
| Region | `East US 2` |
| Container App | `rg-ajitmishra-0084-splunkagent` |
| Container Apps Environment | `cae-splunkagent` |
| Azure Container Registry | `ca15ef92c185acr.azurecr.io` |
| Log Analytics Workspace | `workspace-rgajitmishra0084gogS` |
| Public URL | `https://rg-ajitmishra-0084-splunkagent.orangebeach-011b5a6f.eastus2.azurecontainerapps.io` |

## 5. Current Deployment

| Item | Value |
|---|---|
| Current image | `ca15ef92c185acr.azurecr.io/splunkinvestigator:opsfieldsearch-20260426190756` |
| Current revision | `rg-ajitmishra-0084-splunkagent--0000011` |
| Runtime status | `Running` |
| HTTP verification | `200 OK` |

## 6. Build and Deployment Commands

Build locally:

```powershell
dotnet build SplunkInvestigator_v3.sln
```

Build image in ACR:

```powershell
$tag = "release-$(Get-Date -Format yyyyMMddHHmmss)"
az acr build `
  --registry ca15ef92c185acr `
  --image splunkinvestigator:$tag `
  --image splunkinvestigator:latest .
```

Deploy to Azure Container Apps:

```powershell
$stamp = Get-Date -Format o
az containerapp update `
  -g rg-ajitmishra-0084 `
  -n rg-ajitmishra-0084-splunkagent `
  --image ca15ef92c185acr.azurecr.io/splunkinvestigator:$tag `
  --set-env-vars DEPLOY_STAMP=$stamp
```

Check deployment:

```powershell
az containerapp show `
  -g rg-ajitmishra-0084 `
  -n rg-ajitmishra-0084-splunkagent `
  --query "{latestRevision:properties.latestRevisionName,latestReadyRevision:properties.latestReadyRevisionName,runningStatus:properties.runningStatus,image:properties.template.containers[0].image}" `
  -o json
```

Verify URL:

```powershell
Invoke-WebRequest `
  -Uri "https://rg-ajitmishra-0084-splunkagent.orangebeach-011b5a6f.eastus2.azurecontainerapps.io" `
  -UseBasicParsing
```

## 7. Validation Queries

Run these from the app UI after deployment:

```text
Get log statistics
level=ERROR
show data where currency is GBP
show data where currencu is GBP
index=payments amount=1200
event=GATEWAY_TIMEOUT
show me gateway related issue
status=BLOCKED
trace.traceId=demo-trace-pay-002
```

Expected result highlights:

- `show data where currency is GBP` returns 5 rows.
- `index=payments amount=1200` returns `PAY-20260426-002`.
- `event=GATEWAY_TIMEOUT` returns the payment gateway timeout row.
- `level=ERROR` returns 3 rows.
