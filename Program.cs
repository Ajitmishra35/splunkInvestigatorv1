using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using SplunkInvestigator.Services;
using SplunkInvestigator.Tools;

var builder = WebApplication.CreateBuilder(args);

// ── Blazor Server ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Configuration ──────────────────────────────────────────────────────────
var azureConfig         = builder.Configuration.GetSection("AzureOpenAI");
var endpoint            = azureConfig["Endpoint"]!;
var apiKey              = azureConfig["ApiKey"]!;
var deployment          = azureConfig["DeploymentName"]          ?? "gpt-4o";
var embeddingDeployment = azureConfig["EmbeddingDeploymentName"] ?? "text-embedding-3-small";

// ── Azure OpenAI → IChatClient (Microsoft.Extensions.AI / Agent Framework) ─
builder.Services.AddSingleton<IChatClient>(_ =>
{
    var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    return azureClient
        .GetChatClient(deployment)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation()   // Automatic tool-calling loop
        .Build();
});

// ── IEmbeddingGenerator (Microsoft.Extensions.AI / Agent Framework) ────────
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(_ =>
    new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey))
        .GetEmbeddingClient(embeddingDeployment)
        .AsIEmbeddingGenerator(defaultModelDimensions: 256));

// ── App Services ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<InMemoryVectorStoreService>();
builder.Services.AddSingleton<QdrantVectorStoreService>();
builder.Services.AddSingleton<AzureAISearchVectorStoreService>();
builder.Services.AddSingleton<IVectorStoreService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("VectorStoreSelection");
    var fallback = sp.GetRequiredService<InMemoryVectorStoreService>();
    var provider = config["VectorStore:Provider"] ?? "InMemory";

    IVectorStoreService selected = provider.Trim().ToLowerInvariant() switch
    {
        "qdrant" => sp.GetRequiredService<QdrantVectorStoreService>(),
        "azureaisearch" or "azure-ai-search" or "azuresearch" => sp.GetRequiredService<AzureAISearchVectorStoreService>(),
        "inmemory" or "in-memory" or "memory" => fallback,
        _ => fallback
    };

    if (!ReferenceEquals(selected, fallback) && !selected.IsAvailable)
    {
        logger.LogWarning(
            "Configured vector store '{Provider}' is unavailable. Falling back to in-memory vector search.",
            provider);
        return fallback;
    }

    logger.LogInformation("Using vector store provider: {Provider}", selected.GetType().Name);
    return selected;
});
builder.Services.AddSingleton<LogFileService>();
builder.Services.AddSingleton<SplunkTools>();
builder.Services.AddScoped<AgentService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<SplunkInvestigator.Components.App>()
    .AddInteractiveServerRenderMode();

// ── Qdrant health check (fire-and-forget — never blocks startup) ────────────
_ = Task.Run(() =>
    app.Services.GetRequiredService<IVectorStoreService>().IsAvailable);
_ = Task.Run(async () =>
    await app.Services.GetRequiredService<LogFileService>().InitializeSampleLogsAsync());

app.Run();
