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
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorStoreService>();
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

app.Run();
