using Microsoft.Extensions.Logging;

namespace HybridAgent.Core.RAG;

/// <summary>
/// Fetches content from BereanResource.Api and feeds it into the RAG pipeline.
/// Replaces direct e-Sword file reading for agents configured with ResourceApiBaseUrl.
///
/// Indexing strategy per source type:
///   Bible       — one chunk per verse, with exact (book, chapter, verse) coordinates
///   Commentary  — one chunk per entry, with verse coordinates when available
///   Books       — overlapping chunks within each chapter (prose, no verse coords)
/// </summary>
public static class ApiIndexer
{
    public static async Task IndexAllAsync(
        RagPipeline pipeline,
        BereanResourceApiClient client,
        string language = "en",
        int chunkSize = 500,
        int overlap = 100,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        // Bibles are NOT indexed — verses are fetched on-demand via the lookup_verse tool.
        //await IndexCommentariesAsync(pipeline, client, language, chunkSize, overlap, log, ct); //just index books.
        await IndexBooksAsync(pipeline, client, chunkSize, overlap, log, ct);
    }

    // ── Bible ─────────────────────────────────────────────────────────────────

    public static async Task IndexBiblesAsync(
        RagPipeline pipeline,
        BereanResourceApiClient client,
        string language = "en",
        int chunkSize = 500,
        int overlap = 100,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        var modules = await client.GetBiblesAsync(ct);
        log?.LogInformation("[ApiIndexer] Bibles: {Count} module(s)", modules.Count);

        foreach (var module in modules)
        {
            if (!LanguageMatches(module.Language, language)) continue;

            log?.LogInformation("[ApiIndexer] Indexing Bible: {ModuleId}", module.ModuleId);
            var books = await client.GetBibleBooksAsync(module.ModuleId, ct);

            foreach (var book in books)
            {
                ct.ThrowIfCancellationRequested();
                for (int ch = 1; ch <= book.ChapterCount; ch++)
                {
                    var chapter = await client.GetBibleChapterAsync(module.ModuleId, book.Name, ch, ct);
                    if (chapter is null || chapter.Verses.Count == 0) continue;

                    var chunks = new List<DocumentChunk>();
                    foreach (var verse in chapter.Verses)
                    {
                        if (string.IsNullOrWhiteSpace(verse.Text)) continue;
                        var text = $"{verse.Reference} {verse.Text}";
                        chunks.AddRange(DocumentChunker.Chunk(
                            text, module.ModuleId, chunkSize, overlap,
                            book.Number, ch, verse.Verse, verse.Verse,
                            SourceType.Bible, language));
                    }

                    if (chunks.Count > 0)
                        await pipeline.IndexChunksAsync(chunks, module.ModuleId, ct);
                }
            }

            log?.LogInformation("[ApiIndexer] Bible '{ModuleId}' indexed", module.ModuleId);
        }
    }

    // ── Commentary ────────────────────────────────────────────────────────────

    public static async Task IndexCommentariesAsync(
        RagPipeline pipeline,
        BereanResourceApiClient client,
        string language = "en",
        int chunkSize = 500,
        int overlap = 100,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        var modules = await client.GetCommentariesAsync(ct);
        log?.LogInformation("[ApiIndexer] Commentaries: {Count} module(s)", modules.Count);

        // Reuse the canonical 66-book list from the first available English Bible
        var bookList = await GetCanonicalBooksAsync(client, ct);
        if (bookList.Count == 0)
        {
            log?.LogWarning("[ApiIndexer] No Bible module found to drive commentary indexing");
            return;
        }

        foreach (var module in modules)
        {
            log?.LogInformation("[ApiIndexer] Indexing commentary: {ModuleId}", module.ModuleId);

            foreach (var book in bookList)
            {
                ct.ThrowIfCancellationRequested();
                for (int ch = 1; ch <= book.ChapterCount; ch++)
                {
                    var chapter = await client.GetCommentaryChapterAsync(
                        module.ModuleId, book.Name, ch, language, ct);

                    if (chapter is null || chapter.Entries.Count == 0) continue;

                    var chunks = new List<DocumentChunk>();
                    foreach (var entry in chapter.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Text)) continue;
                        chunks.AddRange(DocumentChunker.Chunk(
                            entry.Text, module.ModuleId, chunkSize, overlap,
                            book.Number, ch, entry.VerseBegin, entry.VerseEnd,
                            SourceType.Commentary, language));
                    }

                    if (chunks.Count > 0)
                        await pipeline.IndexChunksAsync(chunks, module.ModuleId, ct);
                }
            }

            log?.LogInformation("[ApiIndexer] Commentary '{ModuleId}' indexed", module.ModuleId);
        }
    }

    // ── Books ─────────────────────────────────────────────────────────────────

    public static async Task IndexBooksAsync(
        RagPipeline pipeline,
        BereanResourceApiClient client,
        int chunkSize = 500,
        int overlap = 100,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        var books = await client.GetBooksAsync(ct);
        log?.LogInformation("[ApiIndexer] Books: {Count} module(s)", books.Count);

        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
            log?.LogInformation("[ApiIndexer] Indexing book: {Title}", book.Title);

            var chapters = await client.GetBookChaptersAsync(book.ModuleId, ct);

            foreach (var chapter in chapters)
            {
                var content = await client.GetBookChapterAsync(book.ModuleId, chapter.Id, ct);
                if (content is null || content.Paragraphs.Count == 0) continue;

                // Join paragraph plain text; use chapter title as source label for context
                var text = string.Join("\n\n", content.Paragraphs
                    .Where(p => !string.IsNullOrWhiteSpace(p.PlainText))
                    .Select(p => p.PlainText));

                if (string.IsNullOrWhiteSpace(text)) continue;

                var source = $"{book.Title} — {chapter.Title}";
                var chunks = DocumentChunker.Chunk(
                    text, source, chunkSize, overlap,
                    sourceType: SourceType.Book,
                    language: book.Language);

                if (chunks.Count > 0)
                    await pipeline.IndexChunksAsync(chunks, source, ct);
            }

            log?.LogInformation("[ApiIndexer] Book '{Title}' indexed", book.Title);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<List<ApiBibleBook>> GetCanonicalBooksAsync(
        BereanResourceApiClient client, CancellationToken ct)
    {
        var bibles = await client.GetBiblesAsync(ct);
        foreach (var bible in bibles)
        {
            var books = await client.GetBibleBooksAsync(bible.ModuleId, ct);
            if (books.Count > 0) return books;
        }
        return [];
    }

    private static bool LanguageMatches(string moduleLanguage, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage)) return true;
        return moduleLanguage.StartsWith(targetLanguage, StringComparison.OrdinalIgnoreCase);
    }
}
