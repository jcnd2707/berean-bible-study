using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berean.Core.Resources;

// ── Client ────────────────────────────────────────────────────────────────────

/// <summary>
/// Typed HTTP client for the BereanResource.Api.
/// Wraps all endpoints needed for RAG indexing and tool-based word lookup.
/// </summary>
public class BereanResourceApiClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>For tests: supply an HttpClient with a stubbed handler.</summary>
    internal BereanResourceApiClient(HttpClient http) => _http = http;

    public BereanResourceApiClient(string baseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
    }

    // ── Resource discovery ────────────────────────────────────────────────────

    public Task<List<ApiResourceModule>> GetBiblesAsync(CancellationToken ct = default)
        => GetListAsync<ApiResourceModule>("api/resources/bibles", ct);

    public Task<List<ApiResourceModule>> GetCommentariesAsync(CancellationToken ct = default)
        => GetListAsync<ApiResourceModule>("api/resources/commentaries", ct);

    public Task<List<ApiResourceModule>> GetDictionariesAsync(CancellationToken ct = default)
        => GetListAsync<ApiResourceModule>("api/resources/dictionaries", ct);

    public Task<List<ApiBookSummary>> GetBooksAsync(CancellationToken ct = default)
        => GetListAsync<ApiBookSummary>("api/resources/books", ct);

    // ── Bible ─────────────────────────────────────────────────────────────────

    public Task<List<ApiBibleBook>> GetBibleBooksAsync(string moduleId, CancellationToken ct = default)
        => GetListAsync<ApiBibleBook>($"api/bible/{Uri.EscapeDataString(moduleId)}/books", ct);

    public Task<ApiChapterRecord?> GetBibleChapterAsync(
        string moduleId, string bookName, int chapter, CancellationToken ct = default)
        => GetAsync<ApiChapterRecord>(
            $"api/bible/{Uri.EscapeDataString(moduleId)}/{Uri.EscapeDataString(bookName)}/{chapter}", ct);

    /// <summary>Where a Strong's number is used (null when the module carries no Strong's tags).</summary>
    public Task<ApiStrongsOccurrences?> GetStrongsOccurrencesAsync(
        string moduleId, string number, int limit = 20, CancellationToken ct = default)
        => GetAsync<ApiStrongsOccurrences>(
            $"api/bible/{Uri.EscapeDataString(moduleId)}/strongs/{Uri.EscapeDataString(number)}/occurrences?limit={limit}", ct);

    // ── Commentary ────────────────────────────────────────────────────────────

    public Task<ApiCommentaryChapter?> GetCommentaryChapterAsync(
        string moduleId, string bookName, int chapter, string lang = "en", CancellationToken ct = default)
        => GetAsync<ApiCommentaryChapter>(
            $"api/commentary/{Uri.EscapeDataString(moduleId)}/{Uri.EscapeDataString(bookName)}/{chapter}?lang={lang}", ct);

    // ── Cross-references ──────────────────────────────────────────────────────

    /// <summary>Related passages for a verse, strongest first (from the local cross-reference data).</summary>
    public Task<ApiCrossReferenceResult?> GetCrossReferencesAsync(
        string bookName, int chapter, int verse, int limit = 10, CancellationToken ct = default)
        => GetAsync<ApiCrossReferenceResult>(
            $"api/crossreferences/{Uri.EscapeDataString(bookName)}/{chapter}/{verse}?limit={limit}", ct);

    // ── Dictionary ────────────────────────────────────────────────────────────

    public Task<ApiDictionaryEntry?> LookupWordAsync(
        string moduleId, string word, CancellationToken ct = default)
    {
        var param = IsStrongsNumber(word) ? "strongs" : "word";
        return GetAsync<ApiDictionaryEntry>(
            $"api/dictionary/{Uri.EscapeDataString(moduleId)}/lookup?{param}={Uri.EscapeDataString(word)}", ct);
    }

    private static bool IsStrongsNumber(string word) =>
        word.Length >= 2 &&
        word[0] is 'G' or 'H' or 'g' or 'h' &&
        word[1..].All(char.IsDigit);

    public Task<List<ApiDictionaryEntry>> SearchDictionaryAsync(
        string moduleId, string query, int limit = 5, CancellationToken ct = default)
        => GetListAsync<ApiDictionaryEntry>(
            $"api/dictionary/{Uri.EscapeDataString(moduleId)}/search?q={Uri.EscapeDataString(query)}&limit={limit}", ct);

    /// <summary>Entries whose definition gives this transliteration ("nephesh" → H5315).</summary>
    public Task<List<ApiDictionaryEntry>> FindByTransliterationAsync(
        string moduleId, string term, int limit = 3, CancellationToken ct = default)
        => GetListAsync<ApiDictionaryEntry>(
            $"api/dictionary/{Uri.EscapeDataString(moduleId)}/transliteration?q={Uri.EscapeDataString(term)}&limit={limit}", ct);

    // ── Books ─────────────────────────────────────────────────────────────────

    public Task<List<ApiBookChapterSummary>> GetBookChaptersAsync(
        string moduleId, CancellationToken ct = default)
        => GetListAsync<ApiBookChapterSummary>($"api/books/{Uri.EscapeDataString(moduleId)}/chapters", ct);

    public Task<ApiBookChapterContent?> GetBookChapterAsync(
        string moduleId, int chapterId, CancellationToken ct = default)
        => GetAsync<ApiBookChapterContent>(
            $"api/books/{Uri.EscapeDataString(moduleId)}/chapters/{chapterId}", ct);

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<List<T>> GetListAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            var result = await _http.GetFromJsonAsync<List<T>>(url, _json, ct);
            return result ?? [];
        }
        catch (HttpRequestException) { return []; }
        catch (JsonException) { return []; }
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<T>(_json, ct);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}
