using System.Reflection;
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

                // 2, not the default of 1, so CancelMessage isn't queued behind the SendMessage
                // it's meant to stop (D11 in PR1_QUICK_WINS_PLAN.md). This also means a hub method
                // that switches conversations can now run concurrently with a streaming SendMessage
                // on the same connection — see ActiveAnswers.WaitForIdleAsync for how ChatHub keeps
                // the two from interleaving their sends to the client.
                opts.MaximumParallelInvocationsPerClient = 2;
            });

            // Which model answers ("Llm" section). Ollama still does the embeddings.
            var llm = builder.Configuration.GetSection("Llm").Get<LlmConfig>() ?? new LlmConfig();
            llm.OllamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? llm.OllamaEndpoint;
            builder.Services.AddSingleton(llm);

            // The session length limit (Phase 5).
            var sessionLimits = builder.Configuration.GetSection(SessionLimits.SectionName).Get<SessionLimits>() ?? new SessionLimits();
            builder.Services.AddSingleton(sessionLimits);

            builder.Services.AddSingleton<ModelRegistryService>();
            // StudySessionService is Singleton — it holds all active conversations and the shared library
            builder.Services.AddSingleton<StudySessionService>();
            builder.Services.AddSingleton<ActiveAnswers>();

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

            // HistoryTurns must be at least MaxQuestions, or a session could be silently trimmed
            // before it ever reaches its own question limit — raised here, never the other way
            // round (D7: "no silent forgetting inside a session").
            if (llm.HistoryTurns < sessionLimits.MaxQuestions)
            {
                app.Logger.LogWarning(
                    "Llm:HistoryTurns ({Turns}) is below Sessions:MaxQuestions ({Max}) — raising it so no question inside a session is silently forgotten.",
                    llm.HistoryTurns, sessionLimits.MaxQuestions);
                llm.HistoryTurns = sessionLimits.MaxQuestions;
            }

            app.UseCors();

            // Health check and build version (both probed by the HomeOps deploy module after every deploy)
            app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
            app.MapGet("/version", () =>
            {
                // MinVer + SourceLink produce "<version>+<commit>"
                var info = typeof(Program).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
                var plus = info.IndexOf('+');
                return Results.Ok(new
                {
                    version = plus < 0 ? info : info[..plus],
                    commit = plus < 0 ? null : info[(plus + 1)..],
                });
            });

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
