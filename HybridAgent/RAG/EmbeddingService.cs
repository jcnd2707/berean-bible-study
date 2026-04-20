using System.Text;
using System.Text.Json;

namespace HybridAgent.Core.RAG;

/// <summary>
/// Calls Ollama's embedding endpoint to convert text into float vectors.
///
/// Two key optimisations over the original sequential loop:
///
///   1. Batch input — /api/embed accepts a JSON array under "input", letting
///      Ollama embed multiple texts in a single HTTP round-trip. This alone
///      cuts the number of requests by BatchSize (default 32).
///
///   2. Parallel batches — multiple batches are sent concurrently up to
///      MaxConcurrency (default 4). Ollama processes them on its thread pool.
///      Higher values help on machines with more CPU cores or a GPU.
///      Don't set it above ~8 or Ollama starts queuing and you lose the benefit.
///
/// Net result: a corpus that took 60 minutes sequentially typically finishes
/// in 5–10 minutes with these defaults on a mid-range CPU.
///
/// Endpoint auto-detection is retained from the original:
///   Newer Ollama (v0.3+) : POST /api/embed       { model, input: [...] }
///   Older Ollama          : POST /api/embeddings  { model, prompt: "..." }
///   The older endpoint does not support batching — falls back to one-at-a-time.
/// </summary>
public class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly int _batchSize;
    private readonly int _maxConcurrency;

    // Resolved on first use
    private string? _workingEndpoint;

    public EmbeddingService(
        string model = "mxbai-embed-large",
        string baseUrl = "http://localhost:11434",
        int batchSize = 32,
        int maxConcurrency = 4)
    {
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _batchSize = batchSize;
        _maxConcurrency = maxConcurrency;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>Embed a single string. Used at query time.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        await EnsureEndpointAsync(text, ct);
        return await EmbedSingleAsync(text, ct);
    }

    /// <summary>
    /// Embed all chunks in parallel batches, updating Embedding in place.
    /// onProgress is called after each batch completes.
    /// </summary>
    public async Task EmbedChunksAsync(
        List<DocumentChunk> chunks,
        CancellationToken ct = default,
        Action<int, int>? onProgress = null)
    {
        if (chunks.Count == 0) return;

        // Probe endpoint once before launching parallel work
        await EnsureEndpointAsync(chunks[0].Text, ct);

        // Older endpoint doesn't support batching — fall back to sequential
        if (_workingEndpoint == "/api/embeddings")
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].Embedding = await EmbedSingleAsync(chunks[i].Text, ct);
                onProgress?.Invoke(i + 1, chunks.Count);
            }
            return;
        }

        // Split into batches
        var batches = new List<List<DocumentChunk>>();
        for (int i = 0; i < chunks.Count; i += _batchSize)
            batches.Add(chunks.GetRange(i, Math.Min(_batchSize, chunks.Count - i)));

        int completed = 0;
        var sem = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        var tasks = batches.Select(async batch =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var embeddings = await EmbedBatchAsync(
                    batch.Select(c => c.Text).ToList(), ct);

                for (int i = 0; i < batch.Count; i++)
                    batch[i].Embedding = embeddings[i];

                var done = Interlocked.Add(ref completed, batch.Count);
                onProgress?.Invoke(done, chunks.Count);
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    // ── Internals ──────────────────────────────────────────────────────────

    /// <summary>Send multiple texts in one request. Returns one float[] per input.</summary>
    private async Task<List<float[]>> EmbedBatchAsync(
        List<string> texts, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { model = _model, input = texts });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_baseUrl}/api/embed", content, ct);
        resp.EnsureSuccessStatusCode();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("embeddings", out var arr))
            throw new InvalidOperationException(
                $"Unexpected batch embedding response: {raw[..Math.Min(200, raw.Length)]}");

        return arr.EnumerateArray()
                  .Select(e => e.EnumerateArray().Select(v => v.GetSingle()).ToArray())
                  .ToList();
    }

    private async Task<float[]> EmbedSingleAsync(string text, CancellationToken ct)
    {
        object body = _workingEndpoint == "/api/embed"
            ? new { model = _model, input = text }
            : new { model = _model, prompt = text };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{_baseUrl}{_workingEndpoint}", content, ct);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(ct);
        return ParseSingle(raw, _workingEndpoint!);
    }

    private async Task EnsureEndpointAsync(string probeText, CancellationToken ct)
    {
        if (_workingEndpoint is not null) return;

        foreach (var endpoint in new[] { "/api/embed", "/api/embeddings" })
        {
            try
            {
                object body = endpoint == "/api/embed"
                    ? new { model = _model, input = probeText }
                    : new { model = _model, prompt = probeText };

                var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await _http.PostAsync($"{_baseUrl}{endpoint}", content, ct);

                if (response.IsSuccessStatusCode)
                {
                    _workingEndpoint = endpoint;
                    Console.WriteLine($"  [RAG] Embedding endpoint: {endpoint}");
                    return;
                }
            }
            catch (HttpRequestException) { }
        }

        throw new InvalidOperationException(
            $"No working embedding endpoint found at {_baseUrl}. " +
            $"Run: ollama pull {_model}");
    }

    private static float[] ParseSingle(string json, string endpoint)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (endpoint == "/api/embed" &&
            root.TryGetProperty("embeddings", out var arr) &&
            arr.ValueKind == JsonValueKind.Array &&
            arr.GetArrayLength() > 0)
            return [.. arr[0].EnumerateArray().Select(e => e.GetSingle())];

        if (root.TryGetProperty("embedding", out var single) &&
            single.ValueKind == JsonValueKind.Array)
            return [.. single.EnumerateArray().Select(e => e.GetSingle())];

        throw new InvalidOperationException(
            $"Unexpected single embedding response from {endpoint}: " +
            $"{json[..Math.Min(200, json.Length)]}");
    }
}