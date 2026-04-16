using BereanResourceApi.Models;
using BereanResourceApi.Services;
using Microsoft.OpenApi.Models;
using System.ComponentModel.Design;

namespace BereanResourceApi;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        ConfigureServices(builder);

        var app = builder.Build();

        ConfigurePipeline(app);

        app.Run();
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        // ── Configuration ─────────────────────────────────────────────────────
        builder.Services.Configure<BereanResourcesConfig>(
            builder.Configuration.GetSection(BereanResourcesConfig.SectionName));

        // ── Domain services ───────────────────────────────────────────────────
        builder.Services.AddSingleton<ResourceDiscoveryService>();
        builder.Services.AddSingleton<BibleService>();
        builder.Services.AddSingleton<CommentaryService>();
        builder.Services.AddSingleton<DictionaryService>();
        builder.Services.AddSingleton<NotesService>();

        // ── MVC ───────────────────────────────────────────────────────────────
        builder.Services.AddControllers();

        // ── Swagger / OpenAPI ─────────────────────────────────────────────────
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Berean Resource API",
                Version = "v1",
                Description = "Serves Bible text, commentaries, dictionaries and notes from local e-Sword files."
            });
        });

        // ── CORS ──────────────────────────────────────────────────────────────
        var allowedOrigins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();

        builder.Services.AddCors(options =>
        {
            //options.AddDefaultPolicy(policy =>
            //    policy.WithOrigins(allowedOrigins)
            //          .AllowAnyHeader()
            //          .AllowAnyMethod());

            //for testing
            options.AddDefaultPolicy(policy =>
                policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());

        });
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        // Ensure notes DB and table exist before accepting requests
        app.Services.GetRequiredService<NotesService>().EnsureCreated();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "Berean Resource API v1");
                options.RoutePrefix = "swagger";
            });
        }

        app.UseHttpsRedirection();
        app.UseCors();
        app.MapControllers();
    }
}