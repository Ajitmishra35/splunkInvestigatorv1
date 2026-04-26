# Splunk Investigator AI Agent - Management Approval Brief

## Executive Summary

Splunk Investigator AI Agent is an internal AI-assisted investigation tool that helps support, operations, SRE, security, and engineering teams analyze Splunk-exported logs faster. Instead of asking users to manually search multiple log files, correlate reference numbers, read long error timelines, and summarize incident patterns, the tool provides a chat-based investigator experience backed by Azure OpenAI, structured search, and vector search.

The current version is a working prototype built with .NET 10, Blazor Server, Azure OpenAI, Microsoft.Extensions.AI, and Qdrant vector search. It can ingest exported Splunk JSON logs, discover schemas, search by reference numbers, run query-like filters, generate timelines, identify errors, and perform semantic/hybrid investigation across domains such as payments, transfers, loans, cards, fraud, or any log domain with similar JSON structure.

The approval request is to fund and productionize this prototype into an enterprise-ready internal application with secure hosting, identity integration, auditability, monitoring, governance, and controlled rollout.

## Approval Ask

Request approval for:

1. Production hardening of the current Splunk Investigator prototype.
2. Azure hosting and AI/vector infrastructure for pilot and production rollout.
3. Security review, architecture review, and data governance approval.
4. A phased rollout to operations/support teams.
5. Budget for Azure OpenAI usage, vector database/search, application hosting, monitoring, and engineering effort.

Recommended initial approval:

| Phase | Duration | Outcome |
|---|---:|---|
| Phase 1: Enterprise Pilot | 4-6 weeks | Secure internal pilot with limited users and selected Splunk datasets |
| Phase 2: Production MVP | 8-12 weeks | SSO, RBAC, audit logs, CI/CD, observability, container hosting, approved data controls |
| Phase 3: Enterprise Scale | 3-6 months | Splunk API integration, multi-team adoption, cost controls, governance dashboards |

## Business Problem

Incident investigation is still heavily manual. Teams often need to:

- Search Splunk manually using exact field names and SPL queries.
- Reconstruct transaction or event timelines from scattered logs.
- Correlate errors across multiple systems.
- Summarize technical findings for management or business teams.
- Repeat the same investigation workflow across incidents.
- Depend on experienced SMEs who know the right query patterns.

This creates delays during production incidents, increases mean time to resolution, and makes knowledge transfer difficult.

## Proposed Solution

Splunk Investigator provides an AI-assisted investigation interface where users can ask questions in natural language, for example:

- "Find all errors for transaction TXN-20260425-001."
- "Show the complete timeline for this payment reference."
- "Find similar gateway timeout failures."
- "Summarize blocked transfer events from yesterday's export."
- "What are the top error patterns in the payments domain?"

The agent then uses controlled internal tools to retrieve relevant log entries, timeline data, statistics, exact matches, and semantic matches. The AI does not need unrestricted access to systems; it works through approved application tools and controlled data access.

## Why This Benefits The Company

### Faster Incident Resolution

The tool can reduce time spent searching logs, reconstructing timelines, and preparing summaries. This directly supports lower mean time to detect, investigate, and resolve production issues.

### Better Use Of Existing Splunk Investment

Splunk already contains valuable operational data, but extracting insights requires query skill and domain knowledge. This tool makes the existing Splunk data easier to use without replacing Splunk.

### Knowledge Transfer

Junior support engineers can investigate issues using guided natural-language workflows while still relying on structured data from logs. This reduces dependency on a small number of SMEs.

### Consistent Investigation Quality

The agent follows repeatable tool-based workflows:

- Discover schema first.
- Search by reference.
- Build timeline.
- Check errors/warnings.
- Search similar historical incidents.
- Summarize findings.

This improves consistency across teams and incidents.

### Management-Ready Summaries

The tool can produce concise incident summaries from raw logs, helping teams communicate impact, likely cause, and next actions faster.

### Foundation For Enterprise AI Operations

The project creates a reusable pattern for secure enterprise AI:

- Private data access through controlled tools.
- Azure OpenAI integration.
- Vector search for operational knowledge.
- Audit-friendly application layer.
- Replaceable vector backend through `IVectorStoreService`.

## Current Prototype Capabilities

| Capability | Current Status |
|---|---|
| Blazor web UI | Implemented |
| Azure OpenAI chat integration | Implemented |
| Microsoft.Extensions.AI tool calling | Implemented |
| Local Splunk JSON export ingestion | Implemented |
| Schema discovery | Implemented |
| Reference number search | Implemented |
| SPL-like key/value filtering | Implemented |
| Error and statistics tools | Implemented |
| Transaction/event timeline | Implemented |
| Embedding generation | Implemented |
| Qdrant vector search | Implemented |
| Semantic search | Implemented |
| Hybrid exact + semantic search | Implemented |
| Sensitive field filtering before AI context | Implemented at application level |
| Enterprise SSO/RBAC | Not yet implemented |
| Direct Splunk API integration | Not yet implemented |
| Audit logging and compliance reporting | Not yet implemented |
| Production CI/CD and container deployment | Not yet implemented |

## Target Enterprise Architecture

```text
Users
  |
  | HTTPS
  v
Enterprise Access Layer
  - Entra ID / SSO
  - Role-based access control
  - Conditional access
  |
  v
Splunk Investigator Web App
  - .NET / Blazor Server
  - Containerized deployment
  - Internal URL
  - Audit logging
  |
  +----------------------+
  | Agent Orchestration  |
  | Microsoft.Extensions |
  | AI tool calling      |
  +----------------------+
       |              |
       |              |
       v              v
Azure OpenAI       Approved Tools
Chat model         - Schema discovery
Embeddings         - Ref search
Private endpoint   - Query/filter search
optional           - Timeline
                   - Statistics
                   - Semantic/hybrid search
                          |
                          v
                   Log Data Layer
                   - Phase 1: Splunk JSON exports
                   - Phase 2: Splunk API/service account
                   - Phase 3: Event pipeline
                          |
                          v
                   Vector Store
                   - Current: Qdrant
                   - Alternative: Azure AI Search
```

## Recommended Production Architecture

### Application Hosting

Recommended options:

| Option | Best For | Notes |
|---|---|---|
| Azure App Service for Containers | Fast enterprise deployment | Good for web app, managed TLS, scaling, deployment slots |
| Azure Container Apps | Container-native scaling | Good if event-driven scaling is required |
| AKS | Enterprise platform teams | Best if company already standardizes on Kubernetes |

Recommended for MVP: Azure App Service for Containers or Azure Container Apps.

### Identity And Access

Production should use:

- Microsoft Entra ID SSO.
- RBAC by team/domain.
- Conditional access aligned with company policy.
- No anonymous access.
- Admin-only ingestion/configuration screens.

### Secrets And Configuration

Production should use:

- Azure Key Vault for API keys and connection strings.
- Managed Identity where supported.
- No real keys in `appsettings.json`.
- Environment-specific configuration.
- Secret rotation process.

### Data Security

Production controls should include:

- Sensitive field filtering before AI context.
- Data minimization by default.
- Configurable allow/deny list for fields.
- Audit logs for user questions and tool calls.
- Retention policy for chat history and investigation results.
- Private networking where required.
- No training on company data unless explicitly approved by enterprise AI policy.

### Observability

Production should include:

- Application Insights / OpenTelemetry.
- Request tracing.
- Token usage tracking.
- Per-user and per-team cost attribution.
- Error rate and latency dashboards.
- Audit trail for investigation activity.

## Cost Estimate

Actual cost depends on region, model choice, token volume, number of users, log volume, vector database size, and hosting tier. The figures below are planning estimates only and should be validated in the Azure Pricing Calculator and vendor pricing pages before purchase approval.

Pricing references:

- Azure OpenAI pricing: https://azure.microsoft.com/en-us/pricing/details/cognitive-services/openai-service/
- OpenAI API pricing reference: https://platform.openai.com/docs/pricing
- Azure App Service pricing: https://azure.microsoft.com/en-us/pricing/details/app-service/linux/
- Qdrant Cloud pricing: https://qdrant.tech/pricing/

### Main Cost Drivers

| Cost Area | Driver |
|---|---|
| Azure OpenAI chat model | Number of questions, input tokens, output tokens |
| Azure OpenAI embeddings | Number and size of log entries indexed |
| Vector store | Number of vectors, memory, storage, replicas, availability |
| App hosting | CPU/memory tier, number of instances, uptime |
| Monitoring | Logs, traces, retention, dashboards |
| Storage | Uploaded/exported logs, audit logs, investigation history |
| Engineering | Security hardening, integrations, testing, support |

### Example Monthly Run-Rate Scenarios

These are indicative budget bands for management planning, not final vendor quotes.

| Scenario | Users | Usage Pattern | Estimated Monthly Platform Cost |
|---|---:|---|---:|
| Pilot | 10-25 | Limited datasets, business-hours usage | USD 150-500 |
| Department MVP | 50-150 | Daily investigation usage, moderate logs | USD 750-2,500 |
| Enterprise Production | 300-1,000+ | Multi-domain, high availability, monitoring, governance | USD 3,000-12,000+ |

### Cost Formula

Monthly cost can be estimated as:

```text
Total Monthly Cost =
  App Hosting
+ Azure OpenAI Chat Tokens
+ Azure OpenAI Embedding Tokens
+ Vector Store
+ Storage
+ Monitoring
+ Network / Security Services
+ Support / Operations
```

Token cost can be estimated as:

```text
Chat Cost =
  (Input Tokens / 1,000,000 * Input Token Price)
+ (Output Tokens / 1,000,000 * Output Token Price)

Embedding Cost =
  Embedding Tokens / 1,000,000 * Embedding Token Price
```

### Cost Control Measures

To keep cost under control:

- Use smaller models for routine investigation and reserve larger models for complex summaries.
- Limit max response length.
- Cap top-K vector search results.
- Cache repeated investigation results.
- Batch embeddings during ingestion.
- Track token usage by user/team.
- Add monthly budget alerts.
- Archive old vector collections.
- Avoid sending full raw logs to the AI model.

## Benefits Versus Cost

The primary ROI comes from reducing investigation time.

Example:

```text
If 50 engineers/support users save 30 minutes per day:

50 users * 0.5 hours/day * 20 working days = 500 hours saved/month

If blended loaded cost is USD 50/hour:

500 * 50 = USD 25,000 productivity value/month
```

Even if the monthly platform cost is USD 2,500-5,000 for an MVP, the productivity and incident-response value can be materially higher.

Additional ROI comes from:

- Reduced downtime impact.
- Faster customer issue resolution.
- Lower dependency on specialist query knowledge.
- Better auditability of investigation steps.
- Faster management reporting during incidents.

## Risks And Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Sensitive data exposure | Compliance and privacy risk | Field filtering, allowlists, Key Vault, audit logs, approved data policy |
| Incorrect AI summary | Wrong operational decision | Ground answers in retrieved logs, show source events, require human approval |
| Cost overrun | Budget risk | Token budgets, usage dashboards, rate limits, model tiering |
| Unauthorized access | Security risk | Entra ID SSO, RBAC, private networking, conditional access |
| Vendor dependency | Strategic risk | Microsoft.Extensions.AI abstraction and replaceable vector backend |
| Poor adoption | Low ROI | Pilot with real support workflows, training, feedback loop |
| Data freshness gaps | Investigation misses latest logs | Phase 2 direct Splunk API integration |
| Production reliability | User trust issue | HA hosting, monitoring, deployment slots, rollback plan |

## What Must Be Done Before Production

Minimum enterprise readiness checklist:

- Containerize the application.
- Add Dockerfile and CI/CD pipeline.
- Host behind HTTPS on approved Azure service.
- Integrate Microsoft Entra ID SSO.
- Add RBAC by user/team/domain.
- Move secrets to Azure Key Vault.
- Implement audit logging.
- Add token and cost telemetry.
- Add production monitoring and alerts.
- Complete security review.
- Complete data privacy review.
- Validate sensitive field masking with real datasets.
- Define support model and ownership.
- Document disaster recovery and rollback.

## Implementation Roadmap

### Phase 1: Secure Pilot

Duration: 4-6 weeks

Deliverables:

- Containerized application.
- Internal pilot environment.
- Environment-based configuration.
- Key Vault integration.
- Limited Entra ID access.
- Approved pilot datasets.
- Basic usage and cost dashboard.
- Pilot feedback report.

Success criteria:

- 10-25 users can investigate approved logs.
- No secrets in code or container image.
- Users can find timelines and error patterns faster than manual process.
- Initial cost baseline is measured.

### Phase 2: Production MVP

Duration: 8-12 weeks

Deliverables:

- Production hosting.
- SSO and RBAC.
- Audit logging.
- Monitoring and alerting.
- CI/CD pipeline.
- Production security controls.
- Usage/cost controls.
- Operational runbook.
- Direct Splunk API integration assessment.

Success criteria:

- Approved for internal production use.
- Clear ownership and support process.
- Security and compliance sign-off.
- Repeatable deployment process.

### Phase 3: Enterprise Scale

Duration: 3-6 months

Deliverables:

- Direct Splunk integration.
- Multi-domain ingestion.
- Historical incident similarity search.
- Knowledge base integration.
- Executive incident reports.
- Team-level analytics.
- Optional migration to Azure AI Search if preferred by enterprise architecture.

Success criteria:

- Multiple teams onboarded.
- Measurable reduction in investigation time.
- Stable cost per investigation.
- Governance dashboards in place.

## Build Versus Buy

| Option | Pros | Cons |
|---|---|---|
| Build on current prototype | Tailored to company logs, fast iteration, controlled data flow | Requires engineering ownership |
| Buy generic AI observability tool | Vendor support, ready-made features | Higher cost, less customization, data/vendor constraints |
| Do nothing | No new spend | Continued manual investigation cost and slower incident response |

Recommendation: fund the current prototype through a controlled production MVP. It is already aligned to internal Splunk workflows and can be hardened incrementally.

## Decision Needed From CIO / Management

Decision requested:

1. Approve Phase 1 pilot funding.
2. Assign business owner and technical owner.
3. Approve security and data governance review.
4. Approve Azure resources for pilot.
5. Confirm success metrics for production go/no-go.

Suggested success metrics:

- 30-50% reduction in average investigation time for selected use cases.
- 80%+ pilot user satisfaction.
- Zero critical security findings before production.
- Cost per investigation within approved threshold.
- All AI answers traceable to retrieved log evidence.

## Final Recommendation

Splunk Investigator should be approved for an enterprise pilot. The prototype already demonstrates the core technical pattern: AI-assisted investigation with controlled tools, log search, timeline reconstruction, and vector-based semantic search. With focused funding, it can become a secure internal production capability that improves incident response, reduces operational effort, and increases the value extracted from existing Splunk data.

