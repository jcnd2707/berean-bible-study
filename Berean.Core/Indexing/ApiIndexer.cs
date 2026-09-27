using Microsoft.Extensions.Logging;

namespace Berean.Core.Indexing;

/// <summary>
/// Indexes commentaries and books served by BereanResource.Api into the vector store.
///
/// Only modules that are missing from the index are processed, so adding a module to your
/// library (and labelling it in ModuleProfiles) is picked up on the next start without
/// re-embedding everything.
///
/// Bibles are not indexed: verse text is fetched exactly from the API when needed.
/// Dictionaries are not indexed either — they are looked up by word.
/// </summary>
public static class ApiIndexer
{
    /// <summary>The modules that still need indexing.</summary>
    public sealed record IndexPlan(IReadOnlyList<ModuleInfo> Commentaries, IReadOnlyList<ModuleInfo> Books)
    {
        public bool IsEmpty => Commentaries.Count == 0 && Books.Count == 0;
        public int ModuleCount => Commentaries.Count + Books.Count;
    }

    public static async Task<IndexPlan> PlanAsync(
        RagPipeline pipeline,
        ModuleCatalog catalog,
        IReadOnlyCollection<string>? allowedCommentaryModuleIds = null,
        CancellationToken ct = default)
    {
        var doneCommentaries = await pipeline.CompletedModuleIdsAsync(SourceType.Commentary, ct);
        var doneBooks = await pipeline.CompletedModuleIdsAsync(SourceType.Book, ct);

        // An empty or missing allow-list means "all commentaries". (Config binding turns
        // `"AllowedCommentaryModules": null` into an empty list, which previously meant
        // "none" and silently left every commentary out of the index.)
        var allowed = allowedCommentaryModuleIds is { Count: > 0 }
            ? allowedCommentaryModuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var commentaries = catalog.Commentaries
            .Where(m => allowed is null || allowed.Contains(m.ModuleId))
            .Where(m => !doneCommentaries.Contains(m.ModuleId))
            .ToList();
        var books = catalog.Books.Where(m => !doneBooks.Contains(m.ModuleId)).ToList();

        return new IndexPlan(commentaries, books);
    }

    public static async Task IndexAsync(
        RagPipeline pipeline,
        BereanResourceApiClient client,
        IndexPlan plan,
        string language = "en",
        int chunkSize = 400,
        int overlap = 100,
        ILogger? log = null,
        CancellationToken ct = default,
        int? commentaryChunkSize = null,
        int? commentaryOverlap = null)
    {
        if (plan.Commentaries.Count > 0)
        {
            var bookList = await GetCanonicalBooksAsync(client, ct);
            if (bookList.Count == 0)
                log?.LogWarning("[ApiIndexer] No Bible module found to drive commentary indexing");
            else
                foreach (var module in plan.Commentaries)
                    await IndexCommentaryAsync(pipeline, client, module, bookList,
                        language, commentaryChunkSize ?? chunkSize, commentaryOverlap ?? overlap, log, ct);
        }

        foreach (var module in plan.Books)
            await IndexBookAsync(pipeline, client, module, chunkSize, overlap, log, ct);
    }

    // ── Commentary ────────────────────────────────────────────────────────

    private static async Task IndexCommentaryAsync(
        RagPipeline pipeline,
        BereanResourceApiClient client,
        ModuleInfo module,
        List<ApiBibleBook> bookList,
        string language,
        int chunkSize,
        int overlap,
        ILogger? log,
        CancellationToken ct)
    {
        log?.LogInformation("[ApiIndexer] Indexing commentary: {ModuleId} ({Tradition})",
            module.ModuleId, module.Tradition);

        // A previous run may have been cancelled part-way; start that module clean.
        await pipeline.ResetModuleAsync(SourceType.Commentary, module.ModuleId, ct);

        foreach (var book in bookList)
        {
            ct.ThrowIfCancellationRequested();
            for (int ch = 1; ch <= book.ChapterCount; ch++)
            {
                var chapter = await client.GetCommentaryChapterAsync(
                    module.ModuleId, book.Name, ch, language, ct);

                if (chapter is null || chapter.Entries.Count == 0) continue;

                var chunks = new List<DocumentChunk>();
                int entryIndex = 0;
                foreach (var entry in chapter.Entries)
                {
                    entryIndex++;
                    if (string.IsNullOrWhiteSpace(entry.Text)) continue;

                    // The id must be unique per entry: chunk indexes restart at 0 for every
                    // entry, and INSERT OR IGNORE would otherwise drop all but the first.
                    var idPrefix = $"{module.ModuleId}:{book.Number}:{ch}:{entryIndex}";
                    chunks.AddRange(DocumentChunker.Chunk(
                        entry.Text, module.ModuleId, chunkSize, overlap,
                        book.Number, ch, entry.VerseBegin, entry.VerseEnd,
                        SourceType.Commentary, language,
                        moduleId: module.ModuleId, tradition: module.Tradition,
                        idPrefix: idPrefix));
                }

                if (chunks.Count > 0)
                    await pipeline.IndexChunksAsync(chunks, module.ModuleId, ct);
            }
        }

        await pipeline.MarkModuleCompleteAsync(SourceType.Commentary, module.ModuleId, ct);
        log?.LogInformation("[ApiIndexer] Commentary '{ModuleId}' indexed", module.ModuleId);
    }

    // ── Books ─────────────────────────────────────────────────────────────

    private static async Task IndexBookAsync(
        RagPipeline pipeline,
        BereanResourceApiClient client,
        ModuleInfo book,
        int chunkSize,
        int overlap,
        ILogger? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        log?.LogInformation("[ApiIndexer] Indexing book: {Title} ({Tradition})", book.Name, book.Tradition);

        await pipeline.ResetModuleAsync(SourceType.Book, book.ModuleId, ct);

        var chapters = await client.GetBookChaptersAsync(book.ModuleId, ct);
        int chapterIndex = 0;

        foreach (var chapter in chapters)
        {
            ct.ThrowIfCancellationRequested();
            chapterIndex++;
            var content = await client.GetBookChapterAsync(book.ModuleId, chapter.Id, ct);
            if (content is null || content.Paragraphs.Count == 0) continue;

            var text = string.Join("\n\n", content.Paragraphs
                .Where(p => !string.IsNullOrWhiteSpace(p.PlainText))
                .Select(p => p.PlainText));

            if (string.IsNullOrWhiteSpace(text)) continue;

            // Include the explicit chapter number so the agent can cite
            // "Book Title, Chapter N" reliably regardless of chapter title content.
            // The "<Title>, Chapter " prefix is also how legacy rows are matched to a module.
            var source = $"{book.Name}, Chapter {chapterIndex} — {chapter.Title}";
            var chunks = DocumentChunker.Chunk(
                text, source, chunkSize, overlap,
                sourceType: SourceType.Book,
                language: book.Language,
                moduleId: book.ModuleId, tradition: book.Tradition);

            if (chunks.Count > 0)
                await pipeline.IndexChunksAsync(chunks, source, ct);
        }

        await pipeline.MarkModuleCompleteAsync(SourceType.Book, book.ModuleId, ct);
        log?.LogInformation("[ApiIndexer] Book '{Title}' indexed", book.Name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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
}
