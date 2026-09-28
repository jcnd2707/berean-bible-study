using Berean.Agent.Api.Hubs;
using Berean.Agent.Api.Services;

namespace Berean.Agent.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // No-op under dotnet run or IIS. When the Windows Service Control Manager starts the exe,
            // this wires up the service lifecycle. The service runs as the user who is logged in to
            // Claude Code, so the claude CLI finds that login in the user's profile.
            builder.Host.UseWindowsService(o => o.ServiceName = "BereanAgent");

            // ── Services ───────────────────────────────────────────────────────────────

            builder.Services.AddSignalR(opts =>
            {
                opts.MaximumReceiveMessageSize = 64 * 1024; // 64 KB
                opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
            });

            // Which model answers ("Llm" section). Ollama still does the embeddings.
            var llm = builder.Configuration.GetSection("Llm").Get<LlmConfig>() ?? new LlmConfig();
            llm.OllamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? llm.OllamaEndpoint;
            builder.Services.AddSingleton(llm);

            builder.Services.AddSingleton<ModelRegistryService>();
            // StudySessionService is Singleton — it holds all active conversations and the shared library
            builder.Services.AddSingleton<StudySessionService>();

            // Origins the web app may be served from ("Cors:AllowedOrigins"); SignalR needs them listed exactly.
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                          ?? ["http://localhost:4200", "https://localhost:4200"];
            builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));

            builder.Services.AddLogging(l => l
                .AddConsole()
                .SetMinimumLevel(LogLevel.Information));

            var app = builder.Build();

            app.UseCors();

            // Health check
            app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

            // Models the UI can choose between: the configured hosted models plus tool-capable Ollama models
            app.MapGet("/api/models", async (ModelRegistryService registry, CancellationToken ct) =>
                Results.Ok(await registry.GetModelsAsync(ct)));

            // Perspectives configured for this deployment (empty on the public default config).
            app.MapGet("/api/perspectives", (StudySessionService sessions) =>
                Results.Ok(sessions.GetPerspectives()));

            // The profile picker's "keep what's already here?" step (PROFILES_AND_SESSIONS_PLAN.md D6).
            app.MapGet("/api/conversations/unowned", async (StudySessionService sessions) =>
                Results.Ok(new { count = await sessions.CountUnownedConversationsAsync() }));
            app.MapPost("/api/profiles/{id}/adopt-unowned-conversations", async (string id, StudySessionService sessions) =>
            {
                var moved = await sessions.AdoptUnownedConversationsAsync(id);
                return Results.Ok(new { moved, remaining = await sessions.CountUnownedConversationsAsync() });
            });

            // ── SignalR hub ────────────────────────────────────────────────────────────

            app.MapHub<ChatHub>("/hubs/chat");

            // ── Startup info ───────────────────────────────────────────────────────────

            app.Logger.LogInformation("Berean Agent API starting...");
            app.Logger.LogInformation("SignalR hub  : /hubs/chat");
            app.Logger.LogInformation("Health check : /health");
            app.Logger.LogInformation("Model list   : /api/models");

            var (provider, model) = app.Services.GetRequiredService<ModelRegistryService>().Resolve(null);
            app.Logger.LogInformation("Answers from : {Provider} / {Model}", provider, model);
            if (provider is LlmProvider.Anthropic or LlmProvider.OpenAI)
            {
                var envVar = llm.ApiKeyEnvVarFor(provider);
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar)))
                    app.Logger.LogWarning("{EnvVar} is not set — the {Provider} provider cannot answer until it is.", envVar, provider);
            }

            app.Run();
        }
    }
}
