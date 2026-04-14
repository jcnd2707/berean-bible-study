using HybridAgent.API.Hubs;
using HybridAgent.API.Services;


namespace HybridAgent.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            // ── Services ───────────────────────────────────────────────────────────────

            builder.Services.AddSignalR(opts =>
            {
                opts.MaximumReceiveMessageSize = 64 * 1024; // 64 KB
                opts.EnableDetailedErrors = builder.Environment.IsDevelopment();
            });

            // AgentSessionService is Singleton — it holds all active pipelines
            builder.Services.AddSingleton<AgentSessionService>();

            // CORS — required for WPF SignalR client (it uses HTTP for the handshake)
            builder.Services.AddCors(opts => opts.AddDefaultPolicy(p => p
                .WithOrigins("http://localhost", "https://localhost")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));

            builder.Services.AddLogging(l => l
                .AddConsole()
                .SetMinimumLevel(LogLevel.Information));

            // ── REST endpoints for agent/RAG management ────────────────────────────────
            // These complement the SignalR hub for operations that don't need streaming.

            var app = builder.Build();

            app.UseCors();

            // Health check
            app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

            // List available agents
            app.MapGet("/agents", () => Results.Ok(new[]
            {
    new { id = "Car",    name = "Car Diagnostics",    model = "llama3.2:3b" },
    new { id = "Bible",  name = "Bible Research",     model = "llama3.2:3b" },
    new { id = "CSharp", name = "C# Troubleshooting", model = "deepseek-coder:6.7b" },
}));

            // ── SignalR hub ────────────────────────────────────────────────────────────

            app.MapHub<ChatHub>("/hubs/chat");

            // ── Startup info ───────────────────────────────────────────────────────────

            app.Logger.LogInformation("HybridAgent API starting...");
            app.Logger.LogInformation("SignalR hub  : /hubs/chat");
            app.Logger.LogInformation("Health check : /health");
            app.Logger.LogInformation("Agent list   : /agents");

            var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            app.Logger.LogInformation("Cloud model  : {Status}",
                string.IsNullOrEmpty(key) ? "disabled (no OPENAI_API_KEY)" : "enabled");

            app.Run();
        }
    }
}
