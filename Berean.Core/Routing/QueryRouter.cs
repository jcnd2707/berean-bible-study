using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Berean.Core.Routing;

/// <summary>
/// Deterministic pre-router that classifies every incoming query and
/// performs targeted retrieval before the LLM is invoked.
///
/// The LLM only synthesizes — it does not decide how to retrieve.
///
/// Intent categories:
///   Verse       — "John 3:16", "Romans 8:28", "Juan 3:16"
///   Definition  — "what does agape mean", "define pneuma", "G25", "H430"
///   Conceptual  — everything else → semantic search
///   Mixed       — verse + definition detected together
///
/// Two retrieval passes keep the answer neutral:
///   Main pass       everything except Adventist material, capped per module so one large
///                   commentary can't dominate.
///   Adventist pass  only when the SDA toggle is on; the same query restricted to Adventist
///                   modules, returned as a separate block.
///
/// Verse text and verse-pinned commentary come straight from BereanResource.Api (exact,
/// every module, no embeddings). The vector index is used for semantic search only.
/// </summary>
public class QueryRouter
{
    private const int MaxChapterVerses = 40;
    private const int MaxDefinitionChars = 1000;

    private readonly RagPipeline _rag;
    private readonly BereanResourceApiClient? _api;
    private readonly ILogger _log;
    private readonly string _language;
    private readonly RetrievalOptions _cfg;

    private static readonly IReadOnlyList<string> AdventistOnly = [Traditions.Adventist];
    private static readonly IReadOnlyList<SourceType> SearchableTypes = [SourceType.Commentary, SourceType.Book];

    // ── Verse detection ────────────────────────────────────────────────────

    // A chapter number, optionally with :verse and a range (-N) or list (,N,N).
    private static readonly Regex ChapterVersePattern = new(
        @"(?<![\w:])(?<ch>\d{1,3})(?::(?<vs>\d{1,3})(?<tail>-\d{1,3}|(?:,\d{1,3})+)?)?(?![\w:])",
        RegexOptions.Compiled);

    // Words (letters, accents) and bare digits, used to look backwards from a chapter number.
    private static readonly Regex TokenPattern = new(@"[\p{L}\p{M}]+|\d+", RegexOptions.Compiled);

    // Words that can never start a book name; several are also book abbreviations ("is" = Isaiah).
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "in", "of", "at", "to", "by", "as", "is", "do", "an", "a", "the", "and", "for", "but", "or",
        "what", "does", "about", "mean", "talk", "say", "tell", "it", "he", "we", "me", "my", "us", "so",
    };

    // ── Definition detection ───────────────────────────────────────────────

    private static readonly Regex DefinitionPattern = new(
        @"\b(what does|what is|meaning of|define|definition of|means|meaning|significa|significado de|" +
        @"greek word|hebrew word|palabra griega|palabra hebrea|" +
        @"strongs?|strong'?s?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StrongsPattern = new(
        @"\b[GH][1-9]\d{0,3}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The word being asked about, when the question names one.
    private static readonly Regex[] TermPatterns =
    [
        new(@"\b(?:hebrew|greek|aramaic|hebreo|griego)\s+(?:word\s+|palabra\s+)?[""“'‘]?(?<w>\p{L}[\p{L}'’-]{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:meaning of|define|definition of|significado de)\s+(?:the\s+)?(?:word\s+)?[""“'‘]?(?<w>\p{L}[\p{L}'’-]{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bwhat does\s+(?:the\s+)?(?:word\s+)?[""“'‘]?(?<w>\p{L}[\p{L}'’-]{2,})[""”'’]?\s+mean\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bqu[eé] significa\s+[""“'‘]?(?<w>\p{L}[\p{L}'’-]{2,})", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // ── Context tags (sent by the web client) ──────────────────────────────

    private static readonly Regex ContextTagPattern = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    // ── Constructor ────────────────────────────────────────────────────────

    public QueryRouter(
        RagPipeline rag,
        RetrievalOptions ragConfig,
        BereanResourceApiClient? api,
        string language,
        ILogger log)
    {
        _rag = rag;
        _cfg = ragConfig;
        _api = api;
        _language = language;
        _log = log;
    }

    public int ChunkCount => _rag.IndexedChunks;

    // ── Main entry point ───────────────────────────────────────────────────

    public async Task<RetrievalResult> RouteAsync(
        string query,
        RouteOptions options,
        CancellationToken ct = default)
    {
        var msg = QueryMessage.Parse(query);

        // References typed in the question win; otherwise the verse the user has selected.
        var refs = ParseVerses(msg.Question);
        if (refs.Count == 0 && msg.SelectedVerse is not null)
            refs = ParseVerses(msg.SelectedVerse);
        var verseRef = refs.FirstOrDefault();
        var terms = IsDefinitionQuery(msg.Question) ? ExtractLookupTerms(msg.Question) : [];

        var intent = (verseRef is not null, terms.Count > 0) switch
        {
            (true, true) => QueryIntent.Mixed,
            (true, false) => QueryIntent.Verse,
            (false, true) => QueryIntent.Definition,
            (false, false) => QueryIntent.Conceptual,
        };

        _log.LogInformation("[Router] Intent={Intent} mode={Mode} sda={Sda} | {Query}",
            intent, options.Mode, options.IncludeSda, msg.Question.Length > 60 ? msg.Question[..60] + "…" : msg.Question);

        var semanticQuery = verseRef is not null
            ? BuildBookQuery(verseRef, msg.Question)
            : msg.Question;

        var bible = await FetchBibleTextAsync(refs, ct);

        // ── Main pass (neutral) ────────────────────────────────────────────
        var main = new List<ScoredChunk>();

        if (refs.Count > 0)
            main.AddRange(await FetchCommentaryAsync(refs, msg.Question, adventist: false, ct));

        if (terms.Count > 0)
            main.AddRange(await LookupWordsAsync(terms, ct));

        var wantSemantic = intent != QueryIntent.Definition || main.Count == 0;
        if (wantSemantic)
        {
            var embedding = await _rag.EmbedQueryAsync(semanticQuery, ct);

            if (options.Mode == QueryMode.Compare)
                main.AddRange(SearchPerTradition(embedding, main));
            else
                main.AddRange(_rag.Search(embedding, MainFilter(main),
                    topK: verseRef is null ? _cfg.TopK : 3,
                    lambda: _cfg.MmrLambda, candidateK: _cfg.MmrCandidateK));
        }

        // ── Adventist pass (only when asked for) ───────────────────────────
        var adventist = new List<ScoredChunk>();
        if (options.IncludeSda)
        {
            if (refs.Count > 0)
                adventist.AddRange(await FetchCommentaryAsync(refs, msg.Question, adventist: true, ct));

            var embedding = await _rag.EmbedQueryAsync(semanticQuery, ct);
            adventist.AddRange(_rag.Search(embedding, new RetrievalFilter
            {
                SourceTypes = SearchableTypes,
                Language = _language,
                IncludeTraditions = AdventistOnly,
                MaxPerModule = _cfg.MaxPerModule,
                ExistingPerModule = ModuleCounts(adventist),
            }, topK: _cfg.AdventistTopK, lambda: _cfg.MmrLambda, candidateK: _cfg.MmrCandidateK));
        }

        var context = ContextFormatter.Format(bible, main, adventist, _rag.Catalog, _cfg.MaxContextTokens * 4);

        _log.LogInformation("[Router] refs={Refs} main={Main} adventist={Adv} bible={Bible}",
            refs.Count == 0 ? "-" : string.Join("; ", refs.Select(r => r.OriginalText)),
            main.Count, adventist.Count, bible.Count);

        return new RetrievalResult(intent, verseRef, context);
    }

    // ── Semantic search helpers ────────────────────────────────────────────

    private RetrievalFilter MainFilter(IReadOnlyList<ScoredChunk> alreadyChosen) => new()
    {
        SourceTypes = SearchableTypes,
        Language = _language,
        ExcludeTraditions = AdventistOnly,
        MaxPerModule = _cfg.MaxPerModule,
        ExistingPerModule = ModuleCounts(alreadyChosen),
    };

    /// <summary>
    /// Compare mode: the best chunks from each interpretive tradition in the index, so the
    /// answer can set the views side by side. A tradition with nothing above the similarity
    /// floor is skipped rather than given a section just because it exists.
    /// </summary>
    private List<ScoredChunk> SearchPerTradition(float[] embedding, IReadOnlyList<ScoredChunk> alreadyChosen)
    {
        var result = new List<ScoredChunk>();

        var traditions = _rag.IndexedTraditions()
            .Where(Traditions.IsInterpretive)
            .Where(t => t != Traditions.Adventist)   // the Adventist pass handles Adventist
            .OrderBy(t => t)
            .ToList();

        foreach (var tradition in traditions)
        {
            var hits = _rag.Search(embedding, new RetrievalFilter
            {
                SourceTypes = SearchableTypes,
                Language = _language,
                IncludeTraditions = [tradition],
                MaxPerModule = 1,
                MinScore = _cfg.CompareMinScore,
                ExistingPerModule = ModuleCounts(alreadyChosen.Concat(result).ToList()),
            }, topK: _cfg.CompareChunksPerTradition, lambda: _cfg.MmrLambda, candidateK: _cfg.MmrCandidateK);

            _log.LogInformation("[Router] Compare: {Tradition} → {Count} chunk(s)", tradition, hits.Count);
            result.AddRange(hits);
        }

        // Reference (lexical) material feeds the "what the text says" section.
        if (_rag.IndexedTraditions().Contains(Traditions.Lexical))
            result.AddRange(_rag.Search(embedding, new RetrievalFilter
            {
                IncludeTraditions = [Traditions.Lexical],
                Language = _language,
                MaxPerModule = 1,
                MinScore = _cfg.CompareMinScore,
            }, topK: 2, lambda: _cfg.MmrLambda, candidateK: _cfg.MmrCandidateK));

        return result;
    }

    private static Dictionary<string, int> ModuleCounts(IEnumerable<ScoredChunk> chunks)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in chunks)
        {
            var key = ModuleCatalog.ModuleKey(c.Chunk);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    // ── Exact lookups from BereanResource.Api ──────────────────────────────

    private static (int start, int end) VerseSpan(VerseReference r)
    {
        var start = r.Verse ?? 1;
        var end = r.VerseEnd ?? (r.Verses is { Count: > 0 } ? r.Verses.Max() : start);
        return (Math.Min(start, end), Math.Max(start, end));
    }

    private static readonly Regex LeadingVerseNumber = new(@"^\s*\d{1,3}\s+", RegexOptions.Compiled);

    /// <summary>
    /// The verse, range or (capped) chapter from each configured Bible module, for each
    /// reference. Exact text from the module, so the model never has to quote a verse from
    /// memory. Some modules return the same verse several times, so verses are de-duplicated.
    /// </summary>
    private async Task<List<BibleText>> FetchBibleTextAsync(IReadOnlyList<VerseReference> refs, CancellationToken ct)
    {
        if (_api is null || _cfg.AllowedBibleModules.Count == 0 || refs.Count == 0) return [];

        var result = new List<BibleText>();
        foreach (var r in refs)
        {
            var bookName = BibleBookMap.GetFullName(r.BookNumber) ?? r.OriginalText;
            var (start, end) = VerseSpan(r);
            var chapterOnly = r.Verse is null;

            var fetched = await Task.WhenAll(_cfg.AllowedBibleModules.Select(async moduleId =>
            {
                var chapter = await _api.GetBibleChapterAsync(moduleId, bookName, r.Chapter, ct);
                if (chapter is null) return null;

                var verses = chapter.Verses
                    .GroupBy(v => v.Verse).Select(g => g.First())
                    .Where(v => chapterOnly ||
                                (r.IsDiscrete ? r.Verses!.Contains(v.Verse) : v.Verse >= start && v.Verse <= end))
                    .OrderBy(v => v.Verse)
                    .Take(chapterOnly ? MaxChapterVerses : int.MaxValue)
                    .ToList();
                if (verses.Count == 0) return null;

                var reference = chapterOnly ? $"{bookName} {r.Chapter}" : $"{bookName} {r.VerseDisplay}";
                var text = verses.Count == 1
                    ? LeadingVerseNumber.Replace(verses[0].Text, "")
                    : string.Join(" ", verses.Select(v => $"{v.Verse} {LeadingVerseNumber.Replace(v.Text, "")}"));
                return new BibleText(moduleId, reference, text.Trim());
            }));

            result.AddRange(fetched.Where(b => b is not null).Select(b => b!));
        }

        return result;
    }

    /// <summary>
    /// What each commentary says about the referenced passages. One entry per module per
    /// reference, so every tradition gets the same weight however large its commentary is.
    /// A verse or range takes the entries covering it; a whole-chapter reference takes the
    /// entries that best match the question's key words.
    /// </summary>
    private async Task<List<ScoredChunk>> FetchCommentaryAsync(
        IReadOnlyList<VerseReference> refs, string question, bool adventist, CancellationToken ct)
    {
        if (_api is null || refs.Count == 0) return [];

        var allowed = _cfg.AllowedCommentaryModules is { Count: > 0 }
            ? _cfg.AllowedCommentaryModules.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var modules = _rag.Catalog.Commentaries
            .Where(m => (m.Tradition == Traditions.Adventist) == adventist)
            .Where(m => allowed is null || allowed.Contains(m.ModuleId))
            .ToList();

        var terms = QueryTerms(question);
        var chunks = new List<ScoredChunk>();

        foreach (var r in refs)
        {
            var bookName = BibleBookMap.GetFullName(r.BookNumber) ?? r.OriginalText;
            var (start, end) = VerseSpan(r);
            var chapterOnly = r.Verse is null;

            var fetched = await Task.WhenAll(modules.Select(async m =>
            {
                var chapter = await _api.GetCommentaryChapterAsync(m.ModuleId, bookName, r.Chapter, _language, ct);
                if (chapter is null) return null;

                var candidates = chapter.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Text)).ToList();
                var entries = chapterOnly
                    ? BestMatches(candidates, terms, take: 2)
                    : candidates
                        .Where(e => e.VerseBegin <= end && Math.Max(e.VerseBegin, e.VerseEnd) >= start)
                        .OrderBy(e => e.VerseBegin).ToList();
                if (entries.Count == 0) return null;

                var text = TruncateAtSentence(
                    string.Join("\n\n", entries.Select(e => e.Text.Trim())), _cfg.MaxEntryChars);
                var chunk = new DocumentChunk
                {
                    Id = $"api:{m.ModuleId}:{r.BookNumber}:{r.Chapter}:{(chapterOnly ? "ch" : $"{start}-{end}")}",
                    Source = m.ModuleId,
                    ChunkIndex = 0,
                    Text = text,
                    SourceType = SourceType.Commentary,
                    Language = _language,
                    ModuleId = m.ModuleId,
                    Tradition = m.Tradition,
                    BookNumber = r.BookNumber,
                    ChapterBegin = r.Chapter,
                    VerseBegin = chapterOnly ? entries.Min(e => e.VerseBegin) : start,
                    VerseEnd = chapterOnly ? entries.Max(e => Math.Max(e.VerseBegin, e.VerseEnd)) : end,
                };
                return new ScoredChunk(chunk, 1f);
            }));

            chunks.AddRange(fetched.Where(c => c is not null).Select(c => c!));
        }

        // No module may exceed the per-module cap, however many references the question has.
        return _cfg.MaxPerModule <= 0
            ? chunks
            : chunks.GroupBy(c => c.Chunk.ModuleId).SelectMany(g => g.Take(_cfg.MaxPerModule))
                    .OrderBy(c => chunks.IndexOf(c)).ToList();
    }

    // Words too common to say anything about a question (or a commentary entry).
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "does", "this", "that", "with", "from", "have", "which", "about", "would", "could", "should",
        "there", "their", "them", "then", "than", "these", "those", "into", "over", "also", "when", "where",
        "whom", "whose", "will", "shall", "were", "been", "being", "bible", "teach", "teaches", "say", "says",
        "mean", "means", "meaning", "consider", "explain", "tell", "chapter", "verse", "verses", "significance",
        "relationship", "between", "still", "binding", "christians", "who", "why", "how", "does", "and", "the",
    };

    /// <summary>Lower-case content words of the question, minus references and filler.</summary>
    internal static List<string> QueryTerms(string question)
    {
        return TokenPattern.Matches(StripContextTags(question))
            .Select(t => t.Value.ToLowerInvariant())
            .Where(t => t.Length >= 4 && !FillerWords.Contains(t) && BibleBookMap.Resolve(t) is null)
            .Distinct()
            .ToList();
    }

    /// <summary>The entries that mention the question's key words most (ties keep chapter order).</summary>
    private static List<ApiCommentaryEntry> BestMatches(List<ApiCommentaryEntry> entries, List<string> terms, int take)
    {
        if (terms.Count == 0) return [];

        return entries
            .Select(e => (entry: e, score: terms.Sum(t => Occurrences(e.Text, t) is var n and > 0 ? 1 + Math.Log(n) : 0)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(take)
            .Select(x => x.entry)
            .OrderBy(e => e.VerseBegin)
            .ToList();
    }

    private static int Occurrences(string text, string term)
    {
        var count = 0;
        for (var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(term, i + term.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }

    /// <summary>
    /// Definitions for the words the question asks about, from every dictionary and lexicon.
    /// (Hosted providers that run without tools get this up front instead of via lookup_word.)
    /// </summary>
    private async Task<List<ScoredChunk>> LookupWordsAsync(IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (_api is null) return [];

        var perTerm = await Task.WhenAll(terms.Take(3).Select(term =>
            DictionaryLookup.FindAsync(_api, _rag.Catalog.Dictionaries, term, ct)));

        return perTerm.SelectMany(hits => hits).Select(hit =>
        {
            var chunk = new DocumentChunk
            {
                Id = $"api:dict:{hit.Module.ModuleId}:{hit.Topic}",
                Source = hit.Module.ModuleId,
                ChunkIndex = 0,
                Text = TruncateAtSentence(hit.Definition.Trim(), MaxDefinitionChars),
                SourceType = SourceType.Dictionary,
                Language = _language,
                ModuleId = hit.Module.ModuleId,
                Tradition = hit.Module.Tradition,
                Locator = hit.Topic,
            };
            return new ScoredChunk(chunk, 1f);
        }).ToList();
    }

    private static string TruncateAtSentence(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOfAny(['.', '!', '?'], maxChars - 1, Math.Min(maxChars, 400));
        return (cut > maxChars / 2 ? text[..(cut + 1)] : text[..maxChars]) + " …";
    }

    // ── Verse parsing ──────────────────────────────────────────────────────

    /// <summary>The first Bible reference in the text, or null.</summary>
    internal static VerseReference? TryParseVerse(string query) =>
        ParseVerses(query, max: 1).FirstOrDefault();

    /// <summary>
    /// Finds Bible references in free text by looking backwards from each chapter number for
    /// a book name ("Explain John 1:1", "what does 1 John 4:8 mean", "Juan 3:16"). Leading
    /// filler words are dropped, so "Explain John 1:1" resolves to John, not "Explain John".
    /// Returns up to <paramref name="max"/> distinct references in the order they appear.
    /// </summary>
    internal static List<VerseReference> ParseVerses(string query, int max = 3)
    {
        var found = new List<VerseReference>();
        if (string.IsNullOrWhiteSpace(query)) return found;
        query = StripContextTags(query);

        foreach (Match m in ChapterVersePattern.Matches(query))
        {
            // Only whitespace may separate the book name from the chapter number.
            var before = query[..m.Index];
            if (before.Length == 0 || !char.IsWhiteSpace(before[^1])) continue;

            var tokens = TokenPattern.Matches(before).Select(t => t.Value).ToList();
            var bookNum = ResolveBookBefore(tokens);
            if (bookNum is null) continue;

            if (!int.TryParse(m.Groups["ch"].Value, out var chapter)) continue;

            var (verse, verseEnd, verses) = ParseVerseSegment(m.Groups["vs"].Value + m.Groups["tail"].Value);
            var book = BibleBookMap.GetFullName(bookNum.Value) ?? "";
            var reference = new VerseReference(
                bookNum.Value, chapter, verse, verseEnd, verses, $"{book} {m.Value}".Trim());

            if (found.Any(f => f.BookNumber == reference.BookNumber && f.Chapter == reference.Chapter &&
                               f.VerseDisplay == reference.VerseDisplay)) continue;

            found.Add(reference);
            if (found.Count >= max) break;
        }

        return found;
    }

    /// <summary>Longest run of up to four trailing tokens that names a book.</summary>
    private static int? ResolveBookBefore(List<string> tokens)
    {
        for (int take = Math.Min(4, tokens.Count); take >= 1; take--)
        {
            var candidate = tokens.GetRange(tokens.Count - take, take);
            if (StopWords.Contains(candidate[0])) continue;

            // A lone 1-3 right before the name is part of it ("1 Jn" must not fall back to "Jn").
            var before = tokens.Count - take - 1;
            if (before >= 0 && !char.IsDigit(candidate[0][0]) && tokens[before] is "1" or "2" or "3") continue;

            // "1John" → "1 John"
            var text = string.Join(" ", candidate);
            var book = BibleBookMap.Resolve(text);
            if (book is not null) return book;
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

    internal static bool IsDefinitionQuery(string query) =>
        DefinitionPattern.IsMatch(query) || StrongsPattern.IsMatch(query);

    /// <summary>
    /// Words or Strong's numbers the question asks about: "what does G3340 mean" → G3340,
    /// "the Hebrew word hesed" → hesed. Empty when the question doesn't name one.
    /// </summary>
    internal static List<string> ExtractLookupTerms(string query)
    {
        var terms = new List<string>();

        foreach (Match m in StrongsPattern.Matches(query))
            terms.Add(m.Value.ToUpperInvariant());

        foreach (var pattern in TermPatterns)
        {
            var m = pattern.Match(query);
            if (!m.Success) continue;
            var word = m.Groups["w"].Value.Trim('\'', '’', '-');
            if (word.Length > 2 && !StopWords.Contains(word) &&
                !terms.Contains(word, StringComparer.OrdinalIgnoreCase))
                terms.Add(word);
        }

        return terms;
    }

    // ── Query builders ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a semantically rich query by combining the full canonical book name, chapter,
    /// and cleaned user question. A bare reference like "Mat 24:34" embeds poorly against prose;
    /// "Matthew chapter 24 verse 34 generation pass fulfilled" retrieves far more relevant text.
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

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Removes square-bracket context tags from the query before embedding. Tags like
    /// [Translation: akjvstrong] and [Passage: Mat 24] add noise that degrades semantic
    /// retrieval — the embedding model encodes them as content rather than ignoring them.
    /// </summary>
    public static string StripContextTags(string query)
    {
        var stripped = ContextTagPattern.Replace(query, " ");
        return WhitespacePattern.Replace(stripped, " ").Trim();
    }

    /// <summary>The user's question and the verse they have selected, split from the client's context tags.</summary>
    internal sealed record QueryMessage(string Question, string? SelectedVerse)
    {
        public static QueryMessage Parse(string raw)
        {
            string? selected = null;
            foreach (Match tag in ContextTagPattern.Matches(raw))
            {
                var body = tag.Value.Trim('[', ']').Trim();
                if (body.StartsWith("Selected verse:", StringComparison.OrdinalIgnoreCase))
                    selected = body["Selected verse:".Length..].Trim();
            }

            var question = StripContextTags(raw);
            return new QueryMessage(string.IsNullOrWhiteSpace(question) ? raw : question, selected);
        }
    }
}
