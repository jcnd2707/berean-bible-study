using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridAgent.RAG;

/// <summary>
/// Calls Ollama's embedding endpoint to convert text into a float vector.
///
/// Ollama has two endpoints depending on version:
///   Newer (v0.3+) : POST /api/embed       — body: { model, input }   — response: { embeddings: [[...]] }
///   Older         : POST /api/embeddings  — body: { model, prompt }  — response: { embedding: [...] }
///
/// This service probes which one works on first use and sticks with it.
/// You can also force a specific endpoint via the constructor.
///
/// Recommended embedding models (pull one before running):
///   ollama pull nomic-embed-text     (best quality, recommended)
///   ollama pull mxbai-embed-large
///   ollama pull all-minilm
/// </summary>
public class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;

    // Resolved on first successful call — null means not yet probed
    private string? _workingEndpoint;

    public EmbeddingService(
        string model = "nomic-embed-text",
        string baseUrl = "http://localhost:11434")
    {
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// Returns a float vector for a single string.
    /// Automatically discovers the correct endpoint on first call.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // Fast path — endpoint already known
        if (_workingEndpoint is not null)
            return await CallEndpointAsync(_workingEndpoint, text, ct);

        // Probe: try newer /api/embed first, fall back to /api/embeddings
        foreach (var endpoint in new[] { "/api/embed", "/api/embeddings" })
        {
            try
            {
                var result = await CallEndpointAsync(endpoint, text, ct);
                _workingEndpoint = endpoint;
                Console.WriteLine($"  [RAG] Using embedding endpoint: {endpoint}");
                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 404 — try the next one
            }
        }

        throw new InvalidOperationException(
            $"Neither /api/embed nor /api/embeddings returned a valid response from {_baseUrl}. " +
            $"Make sure Ollama is running and '{_model}' is pulled: ollama pull {_model}");
    }

    /// <summary>
    /// Embeds a batch of chunks, updating each chunk's Embedding in place.
    /// </summary>
    public async Task EmbedChunksAsync(
        List<DocumentChunk> chunks,
        CancellationToken ct = default,
        Action<int, int>? onProgress = null)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].Embedding = await EmbedAsync(chunks[i].Text, ct);
            onProgress?.Invoke(i + 1, chunks.Count);
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private async Task<float[]> CallEndpointAsync(
        string endpoint, string text, CancellationToken ct)
    {
        // /api/embed uses "input", /api/embeddings uses "prompt"
        object body = endpoint == "/api/embed"
            ? new { model = _model, input = text }
            : new { model = _model, prompt = text };

        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{_baseUrl}{endpoint}", content, ct);

        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(ct);
        return ParseEmbedding(raw, endpoint);
    }

    private static float[] ParseEmbedding(string json, string endpoint)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // /api/embed returns: { "embeddings": [[0.1, 0.2, ...]] }
        if (endpoint == "/api/embed" &&
            root.TryGetProperty("embeddings", out var embeddings) &&
            embeddings.ValueKind == JsonValueKind.Array &&
            embeddings.GetArrayLength() > 0)
        {
            return [.. embeddings[0].EnumerateArray().Select(e => e.GetSingle())];
        }

        // /api/embeddings returns: { "embedding": [0.1, 0.2, ...] }
        if (root.TryGetProperty("embedding", out var embedding) &&
            embedding.ValueKind == JsonValueKind.Array)
        {
            return [.. embedding.EnumerateArray().Select(e => e.GetSingle())];
        }

        throw new InvalidOperationException(
            $"Unexpected embedding response shape from {endpoint}: {json[..Math.Min(200, json.Length)]}");
    }
}