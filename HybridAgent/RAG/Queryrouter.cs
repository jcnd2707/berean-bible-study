using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace HybridAgent.Core.RAG;

/// <summary>
/// Deterministic pre-router that classifies every incoming query and
/// performs targeted retrieval before the LLM is invoked.
///
/// The LLM only synthesizes — it does not decide how to retrieve.
///
/// Intent categories:
///   Verse       — "John 3:16", "Romans 8:28", "Juan 3:16"
///   Definition  — "what does agape mean", "define pneuma", "G25", "H430"
///   Conceptual  — everything else → semantic RAG search
///   Mixed       — verse + definition detected together
///
/// Output is a RetrievalResult containing pre-fetched context
/// that is injected into DiagnosticAgent.ChatAsync() as ragContext.
/// </summary>
public class QueryRouter
{
    private readonly RagPipeline _rag;
    private readonly ILogger _log;
    private readonly string _language;
    private readonly AgentRagConfig _ragConfig;

    // ── Verse detection pattern ────────────────────────────────────────────
    private static readonly Regex VersePattern = new(
        @"(?<!\w)" +
        @"(?!(?:on|in|of|at|to|by|as|is|do|an|a|the|and|for|but|or|what|does|about|mean|talk|say|tell)\b)" +
        @"(?<book>(?:[1-3]\s*)?[A-Za-z]+(?:\s[A-Za-z]+){0,2})\s+" +
        @"(?<ch>\d{1,3})(?::(?<vs>\d{1,3})(?:-(?<ve>\d{1,3})|(?:,\d{1,3})+)?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LooseVerseHint = new(
        @"\b(?:in|from|at|on)\s+(?<book>(?:[1-3]\s*)?[A-Za-z]+(?:\s[A-Za-záéíóúüñÁÉÍÓÚÜÑ]+){0,2})\s+" +
        @"(?<ch>\d{1,3})(?::(?<vs>\d{1,3}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Definition detection patterns ──────────────────────────────────────
    private static readonly Regex DefinitionPattern = new(
        @"\b(what does|what is|meaning of|define|definition of|means|meaning|significa|significado de|" +
        @"greek word|hebrew word|palabra griega|palabra hebrea|" +
        @"strongs?|strong'?s?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StrongsPattern = new(
        @"\b[GH][1-9]\d{0,3}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Context tag pattern ────────────────────────────────────────────────
    private static readonly Regex ContextTagPattern = new(
        @"\[[^\]]*\]",
        RegexOptions.Compiled);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled);

    // ── Constructor ────────────────────────────────────────────────────────

    public QueryRouter(
        RagPipeline rag,
        AgentRagConfig ragConfig,
        string language,
        ILogger log)
    {
        _rag = rag;
        _ragConfig = ragConfig;
        _language = language;
        _log = log;
    }

    public int ChunkCount => _rag.IndexedChunks;

    // ── Main entry point ───────────────────────────────────────────────────

    public async Task<RetrievalResult> RouteAsync(
        string query,
        CancellationToken ct = default)
    {
        var verseRef = TryParseVerse(query);
        var isDefn = IsDefinitionQuery(query);

        QueryIntent intent = (verseRef is not null, isDefn) switch
        {
            (true, true) => QueryIntent.Mixed,
            (true, false) => QueryIntent.Verse,
            (false, true) => QueryIntent.Definition,
            (false, false) => QueryIntent.Conceptual,
        };

        _log.LogInformation("[Router] Intent={Intent} | {Query}",
            intent, query.Length > 60 ? query[..60] + "…" : query);

        return intent switch
        {
            QueryIntent.Verse => await HandleVerseAsync(verseRef!, query, ct),
            QueryIntent.Definition => HandleDefinition(query),
            QueryIntent.Mixed => await HandleMixedAsync(verseRef!, query, ct),
            QueryIntent.Conceptual => await HandleConceptualAsync(query, ct),
            _ => new RetrievalResult(intent, null),
        };
    }

    // ── Intent handlers ────────────────────────────────────────────────────

    private async Task<RetrievalResult> HandleVerseAsync(
        VerseReference verseRef,
        string query,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();

        // 1. Bible — verse-pinned
        var bibleCtx = _rag.BuildVerseContext(
            verseRef.BookNumber, verseRef.Chapter, verseRef.Verse ?? 1,
            SourceType.Bible, _language);

        // Fallback to English if primary language returns nothing
        if (bibleCtx is null && _language != "en")
            bibleCtx = _rag.BuildVerseContext(
                verseRef.BookNumber, verseRef.Chapter, verseRef.Verse ?? 1,
                SourceType.Bible, "en");

        if (bibleCtx is not null)
            sb.AppendLine(bibleCtx);

        // 2. Commentary — verse-pinned with semantic fallback
        var cmtCtx = _rag.BuildVerseContext(
            verseRef.BookNumber, verseRef.Chapter, verseRef.Verse ?? 1,
            SourceType.Commentary, _language);

        if (cmtCtx is null && verseRef.Verse.HasValue)
        {
            var cmtQuery = BuildBookQuery(verseRef, query: null);
            _log.LogInformation("[Router] Commentary embedding query: {Query}", cmtQuery);

            cmtCtx = await _rag.BuildContextAsync(
                cmtQuery,
                topK: 3,
                lambda: _ragConfig.MmrLambda,
                candidateK: 30,
                sourceType: SourceType.Commentary,
                language: _language,
                ct: ct);
        }

        if (cmtCtx is not null)
            sb.AppendLine(cmtCtx);

        // 3. EGW / prose books — semantically enriched query
        // A bare verse reference embeds poorly against EGW prose.
        // Combining the full book name, chapter, and user question produces
        // far more relevant retrieval from the EGW corpus.
        var booksQuery = BuildBookQuery(verseRef, query);
        _log.LogInformation("[Router] Books embedding query: {Query}", booksQuery);

        var booksCtx = await _rag.BuildContextAsync(
       booksQuery,
       topK: 5,
       lambda: _ragConfig.MmrLambda,
       candidateK: 200,
       sourceType: SourceType.Book,
       language: _language,
       ct: ct);

        if (booksCtx is not null)
            sb.AppendLine(booksCtx);

        _log.LogInformation(
            "[Router] Verse {Ref}: bible={HasBible} commentary={HasCmt} books={HasBooks}",
            verseRef.OriginalText,
            bibleCtx is not null,
            cmtCtx is not null,
            booksCtx is not null);

        return new RetrievalResult(
            QueryIntent.Verse,
            sb.Length > 0 ? sb.ToString() : null,
            verseRef);
    }

    private RetrievalResult HandleDefinition(string query)
    {
        // Definition queries are handled entirely by the lookup_word tool
        // registered in AgentFactory — signal the intent but don't pre-fetch,
        // because the tool call in the agent loop will do the actual retrieval.
        _log.LogInformation("[Router] Definition query — delegating to lookup tools");
        return new RetrievalResult(QueryIntent.Definition, null);
    }

    private async Task<RetrievalResult> HandleMixedAsync(
      VerseReference verseRef,
      string query,
      CancellationToken ct)
    {
        // Verse retrieval already handles bible + commentary + books.
        // No extra pass needed — avoids duplicate reference material.
        var verseResult = await HandleVerseAsync(verseRef, query, ct);
        return verseResult with { Intent = QueryIntent.Mixed };
    }

    private async Task<RetrievalResult> HandleConceptualAsync(
        string query,
        CancellationToken ct)
    {
        // Strip context strip tags before embedding — tags like
        // [Translation: akjvstrong] add noise that degrades retrieval quality
        // because they embed as content rather than query intent.
        var cleanQuery = StripContextTags(query);
        _log.LogInformation("[Router] Conceptual embedding query: {Query}", cleanQuery);

        var ctx = await _rag.BuildMultiSourceContextAsync(
            cleanQuery,
            sourceTypes: [SourceType.Commentary, SourceType.Topic, SourceType.Book],
            topKPerType: _ragConfig.TopK / 4 + 1,
            lambda: _ragConfig.MmrLambda,
            language: _language,
            ct: ct);

        _log.LogInformation("[Router] Conceptual: context={HasCtx}", ctx is not null);
        return new RetrievalResult(QueryIntent.Conceptual, ctx);
    }

    // ── Verse parsing ──────────────────────────────────────────────────────

    private static VerseReference? TryParseVerse(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var normalized = CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(query.ToLowerInvariant());

        var matches = VersePattern.Matches(normalized);
        if (matches.Count == 0)
            matches = LooseVerseHint.Matches(normalized);

        foreach (Match m in matches)
        {
            var bookName = m.Groups["book"].Value.Trim();
            var bookNum = BibleBookMap.Resolve(bookName);
            if (bookNum is null) continue;

            if (!int.TryParse(m.Groups["ch"].Value, out var chapter)) continue;

            var (verse, verseEnd, verses) = ParseVerseSegment(m.Groups["vs"].Value);

            return new VerseReference(
                bookNum.Value, chapter, verse, verseEnd, verses, m.Value.Trim());
        }

        return null;
    }

    private static (int? start, int? end, List<int>? discrete) ParseVerseSegment(string vs)
    {
        if (string.IsNullOrWhiteSpace(vs)) return (null, null, null);

        // Discrete list: "16,20" or "16,20,22"
        if (vs.Contains(',') && !vs.Contains('-'))
        {
            var list = vs.Split(',')
                .Select(v => int.TryParse(v.Trim(), out var n) ? n : (int?)null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();
            return (list.First(), null, list);
        }

        // Range: "16-18"
        if (vs.Contains('-'))
        {
            var parts = vs.Split('-');
            int.TryParse(parts[0].Trim(), out var start);
            int.TryParse(parts[1].Trim(), out var end);
            return (start, end, null);
        }

        // Single verse
        return (int.TryParse(vs.Trim(), out var single) ? single : null, null, null);
    }

    private static bool IsDefinitionQuery(string query) =>
        DefinitionPattern.IsMatch(query) || StrongsPattern.IsMatch(query);

    // ── Query builders ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a semantically rich query for EGW book retrieval by combining
    /// the full canonical book name, chapter, and cleaned user question.
    ///
    /// A bare reference like "Mat 24:34" embeds poorly against EGW prose.
    /// "Matthew chapter 24 verse 34 generation pass fulfilled end times"
    /// retrieves far more relevant passages from the EGW corpus.
    /// </summary>
    private static string BuildBookQuery(VerseReference verseRef, string? query)
    {
        var parts = new List<string>();

        var fullBookName = BibleBookMap.GetFullName(verseRef.BookNumber)
                           ?? verseRef.OriginalText;

        parts.Add($"{fullBookName} chapter {verseRef.Chapter}");

        if (verseRef.Verse.HasValue)
            parts.Add($"verse {verseRef.Verse}");

        if (!string.IsNullOrWhiteSpace(query))
        {
            var clean = StripContextTags(query);
            if (!string.IsNullOrWhiteSpace(clean))
                parts.Add(clean);
        }

        var result = string.Join(" ", parts);
        return result;
    }

    /// <summary>
    /// Removes square-bracket context strip tags from the query before
    /// embedding. Tags like [Translation: akjvstrong] and [Passage: Mat 24]
    /// add noise that degrades semantic retrieval quality — the embedding
    /// model encodes them as content rather than ignoring them.
    /// </summary>
    private static string StripContextTags(string query)
    {
        var stripped = ContextTagPattern.Replace(query, " ");
        return WhitespacePattern.Replace(stripped, " ").Trim();
    }
}

// ── Supporting types ───────────────────────────────────────────────────────

public enum QueryIntent { Verse, Definition, Conceptual, Mixed }

public record VerseReference(
    int BookNumber,
    int Chapter,
    int? Verse,
    int? VerseEnd,
    List<int>? Verses,
    string OriginalText)
{
    public bool IsRange => Verse.HasValue && VerseEnd.HasValue;
    public bool IsDiscrete => Verses is { Count: > 1 };
    public bool IsSingle => !IsRange && !IsDiscrete;

    public string VerseDisplay => this switch
    {
        { IsRange: true } => $"{Chapter}:{Verse}-{VerseEnd}",
        { IsDiscrete: true } => $"{Chapter}:{string.Join(",", Verses!)}",
        { Verse: not null } => $"{Chapter}:{Verse}",
        _ => $"{Chapter}"
    };
}

public record RetrievalResult(
    QueryIntent Intent,
    string? Context,
    VerseReference? VerseRef = null);