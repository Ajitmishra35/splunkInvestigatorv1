using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using SplunkInvestigator.Services;
using SplunkInvestigator.Tools;

var builder = WebApplication.CreateBuilder(args);

// ── Blazor Server ──────────────────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Configuration ──────────────────────────────────────────────────────────
var azureConfig = builder.Configuration.GetSection("AzureOpenAI");
var endpoint = azureConfig["Endpoint"]!;
var apiKey = azureConfig["ApiKey"]!;
var deployment = azureConfig["DeploymentName"] ?? "gpt-4o";

// ── Azure OpenAI → Microsoft.Extensions.AI IChatClient ────────────────────
// This is the new Microsoft.Extensions.AI unified abstraction pattern
builder.Services.AddSingleton<IChatClient>(_ =>
{
    var azureClient = new AzureOpenAIClient(new Uri(endpoint), new Azure.AzureKeyCredential(apiKey));

    // Wrap in Microsoft.Extensions.AI adapter with tool-calling support
    return azureClient
        .GetChatClient(deployment)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation()   // Enables automatic tool-calling loop
        .Build();
});

// ── App Services ───────────────────────────────────────────────────────────
builder.Services.AddSingleton<LogFileService>();   // Reads exported log files
builder.Services.AddSingleton<SplunkTools>();       // Agent tools
builder.Services.AddScoped<AgentService>();         // Scoped per Blazor circuit

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

app.Run();
