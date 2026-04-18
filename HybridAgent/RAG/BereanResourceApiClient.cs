using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridAgent.Core.RAG;

// ── Response DTOs (mirror BereanResource.Api JSON shapes) ────────────────────

public record ApiResourceModule(string ModuleId, string Name, string Language, string FilePath);
public record ApiBookSummary(string ModuleId, string Title, string Author, string Publisher, string Language);
public record ApiBibleBook(int Number, string Name, string Abbreviation, int ChapterCount);
public record ApiVerseRecord(int Book, int Chapter, int Verse, string BookName, string Reference, string Text);
public record ApiChapterRecord(int Book, string BookName, int Chapter, string ModuleId, List<ApiVerseRecord> Verses);
public record ApiCommentaryEntry(int Book, string BookName, int Chapter, int VerseBegin, int VerseEnd, string Reference, string? Marker, string Text);
public record ApiCommentaryChapter(string ModuleId, int Book, string BookName, int Chapter, List<ApiCommentaryEntry> Entries);
public record ApiDictionaryEntry(string Topic, string Definition);
public record ApiBookChapterSummary(int Id, int ChapterNumber, string Title, int OrderIndex);
public record ApiBookParagraph(int OrderIndex, string CssClass, string Content, string PlainText);
public record ApiBookChapterContent(string ModuleId, int ChapterId, int ChapterNumber, string ChapterTitle, List<ApiBookParagraph> Paragraphs);

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

    // ── Commentary ────────────────────────────────────────────────────────────

    public Task<ApiCommentaryChapter?> GetCommentaryChapterAsync(
        string moduleId, string bookName, int chapter, string lang = "en", CancellationToken ct = default)
        => GetAsync<ApiCommentaryChapter>(
            $"api/commentary/{Uri.EscapeDataString(moduleId)}/{Uri.EscapeDataString(bookName)}/{chapter}?lang={lang}", ct);

    // ── Dictionary ────────────────────────────────────────────────────────────

    public Task<ApiDictionaryEntry?> LookupWordAsync(
        string moduleId, string word, CancellationToken ct = default)
        => GetAsync<ApiDictionaryEntry>(
            $"api/dictionary/{Uri.EscapeDataString(moduleId)}/lookup?word={Uri.EscapeDataString(word)}", ct);

    public Task<List<ApiDictionaryEntry>> SearchDictionaryAsync(
        string moduleId, string query, int limit = 5, CancellationToken ct = default)
        => GetListAsync<ApiDictionaryEntry>(
            $"api/dictionary/{Uri.EscapeDataString(moduleId)}/search?q={Uri.EscapeDataString(query)}&limit={limit}", ct);

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
