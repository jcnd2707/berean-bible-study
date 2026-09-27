using System.Text.Json;

namespace Berean.Agent.Api.Services;

public record ModelInfo(string Id, string Name, bool IsDefault, bool SupportsTools, string Provider);

/// <summary>
/// The models the UI can choose between: the configured hosted models (Claude Code, Anthropic
/// or OpenAI) plus any tool-capable Ollama models that are installed. Also decides which
/// provider serves a given model id.
/// </summary>
public class ModelRegistryService
{
    private readonly LlmConfig _llm;
    private readonly string _ollamaEndpoint;
    private readonly string _ollamaDefaultModel;
    private readonly IReadOnlyList<string> _toolCompatiblePrefixes;
    private readonly ILogger<ModelRegistryService> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private IReadOnlyList<ModelInfo>? _ollamaCache;

    public ModelRegistryService(LlmConfig llm, IConfiguration config, ILogger<ModelRegistryService> log)
    {
        _llm = llm;
        _ollamaEndpoint = llm.OllamaEndpoint.TrimEnd('/');
        _ollamaDefaultModel = config["Ollama:DefaultModel"] ?? "llama3.1:8b";
        _toolCompatiblePrefixes = config.GetSection("Ollama:ToolCompatibleModels")
            .Get<string[]>() ?? ["llama3.1", "llama3.2", "qwen2.5", "mistral"];
        _log = log;
    }

    public LlmConfig Llm => _llm;

    /// <summary>Which provider and model id serve a request for <paramref name="modelId"/> (null = the default).</summary>
    public (LlmProvider Provider, string Model) Resolve(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var hosted = _llm.Provider != LlmProvider.Ollama
                ? _llm.HostedModels.FirstOrDefault(m => m.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                : null;
            return hosted is not null ? (_llm.Provider, hosted) : (LlmProvider.Ollama, modelId);
        }

        return _llm.Provider != LlmProvider.Ollama
            ? (_llm.Provider, _llm.Model)
            : (LlmProvider.Ollama, _ollamaDefaultModel);
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken ct = default)
    {
        var result = new List<ModelInfo>();
        var (defaultProvider, defaultModel) = Resolve(null);

        if (_llm.Provider != LlmProvider.Ollama)
        {
            foreach (var id in _llm.HostedModels)
                result.Add(new ModelInfo(id, $"{id} ({Describe(_llm.Provider)})",
                    IsDefault: defaultProvider == _llm.Provider && id.Equals(defaultModel, StringComparison.OrdinalIgnoreCase),
                    SupportsTools: _llm.Provider != LlmProvider.ClaudeCode,
                    Provider: _llm.Provider.ToString()));
        }

        foreach (var m in await GetOllamaModelsAsync(ct))
            result.Add(m with { IsDefault = defaultProvider == LlmProvider.Ollama && m.Id == defaultModel });

        // Never leave the list without a default.
        if (result.Count > 0 && !result.Any(m => m.IsDefault))
            result[0] = result[0] with { IsDefault = true };

        return result;
    }

    private static string Describe(LlmProvider p) => p switch
    {
        LlmProvider.ClaudeCode => "Claude Code",
        LlmProvider.Anthropic => "Anthropic API",
        LlmProvider.OpenAI => "OpenAI API",
        _ => p.ToString(),
    };

    // ── Ollama ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ModelInfo>> GetOllamaModelsAsync(CancellationToken ct)
    {
        if (_ollamaCache is not null) return _ollamaCache;

        var installed = await FetchInstalledModelsAsync(ct);
        var result = new List<ModelInfo>();

        foreach (var modelId in installed)
        {
            if (!await CheckToolSupportAsync(modelId, ct)) continue;
            result.Add(new ModelInfo(modelId, $"{modelId} (local)", IsDefault: false, SupportsTools: true, Provider: nameof(LlmProvider.Ollama)));
        }

        // Only cache a real answer; if Ollama was unreachable, ask again next time.
        if (installed.Count > 0) _ollamaCache = result;
        _log.LogInformation("[Models] {Count} tool-compatible Ollama model(s) found", result.Count);
        return result;
    }

    private async Task<List<string>> FetchInstalledModelsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync($"{_ollamaEndpoint}/api/tags", ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement
                .GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Models] Could not reach Ollama at {Endpoint}", _ollamaEndpoint);
            return [];
        }
    }

    private async Task<bool> CheckToolSupportAsync(string modelId, CancellationToken ct)
    {
        // Try Ollama's capabilities field first (available in Ollama v0.3+)
        try
        {
            var body = JsonSerializer.Serialize(new { name = modelId });
            var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync($"{_ollamaEndpoint}/api/show", content, ct);

            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("capabilities", out var caps))
                {
                    return caps.EnumerateArray()
                        .Any(c => c.GetString()?.Equals("tools", StringComparison.OrdinalIgnoreCase) == true);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[Models] /api/show failed for {Model}, falling back to allowlist", modelId);
        }

        // Fall back to prefix allowlist
        return _toolCompatiblePrefixes.Any(prefix =>
            modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
