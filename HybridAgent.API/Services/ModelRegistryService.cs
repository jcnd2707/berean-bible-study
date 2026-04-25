using System.Text.Json;

namespace HybridAgent.API.Services;

public record ModelInfo(string Id, string Name, bool IsDefault, bool SupportsTools);

public class ModelRegistryService
{
    private readonly string _ollamaEndpoint;
    private readonly string _defaultModel;
    private readonly IReadOnlyList<string> _toolCompatiblePrefixes;
    private readonly ILogger<ModelRegistryService> _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private IReadOnlyList<ModelInfo>? _cache;

    public ModelRegistryService(IConfiguration config, ILogger<ModelRegistryService> log)
    {
        _ollamaEndpoint = (config["Ollama:Endpoint"] ?? "http://localhost:11434").TrimEnd('/');
        _defaultModel = config["Ollama:DefaultModel"] ?? "llama3.1:8b";
        _toolCompatiblePrefixes = config.GetSection("Ollama:ToolCompatibleModels")
            .Get<string[]>() ?? ["llama3.1", "llama3.2", "qwen2.5", "mistral"];
        _log = log;
    }

    public async Task<IReadOnlyList<ModelInfo>> GetToolCompatibleModelsAsync(CancellationToken ct = default)
    {
        if (_cache is not null) return _cache;

        var installed = await FetchInstalledModelsAsync(ct);
        var result = new List<ModelInfo>();

        foreach (var modelId in installed)
        {
            var supportsTools = await CheckToolSupportAsync(modelId, ct);
            if (!supportsTools) continue;

            result.Add(new ModelInfo(
                Id: modelId,
                Name: modelId,
                IsDefault: modelId == _defaultModel,
                SupportsTools: true));
        }

        // If nothing matched the default, mark the first entry as default
        if (result.Count > 0 && !result.Any(m => m.IsDefault))
            result[0] = result[0] with { IsDefault = true };

        _cache = result;
        _log.LogInformation("[Models] {Count} tool-compatible model(s) found", result.Count);
        return _cache;
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
