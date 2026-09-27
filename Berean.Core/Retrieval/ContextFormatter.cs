using System.Text;
using System.Text.RegularExpressions;

namespace Berean.Core.Retrieval;

/// <summary>Exact verse text fetched from a Bible module (never embedded, never from memory).</summary>
public sealed record BibleText(string ModuleId, string Reference, string Text);

/// <summary>
/// One numbered source in the prompt. <see cref="Id"/> is what the model cites ("S1", "A2"), and
/// the rest is what a UI needs to render a clickable chip (see the Sources hub event).
/// </summary>
public sealed record ContextSource(
    string Id,
    string Kind,                 // "commentary" | "book" | "dictionary"
    string ModuleId,
    string DisplayName,
    string Tradition,
    string? Era,
    string Label,                // "Barnes' Notes on the Bible (Evangelical, 19th c.) — John 3:16"
    int? BookNumber,
    int? Chapter,
    int? Verse,
    int? BookChapterIndex,       // books: the "Chapter N" in the chunk source
    ScoredChunk Scored);

/// <summary>The retrieved material, formatted for the prompt, plus the sources it cites.</summary>
public sealed record FormattedContext(
    string Text,
    string? Main,
    string? Adventist,
    IReadOnlyList<ContextSource> Sources);

/// <summary>
/// Turns retrieved chunks into the prompt block. Every source is numbered and tagged with its
/// tradition so the model can say who holds a view, and Adventist material sits in its own
/// block that the prompt only allows the model to use when the SDA toggle is on.
///
///   VERSE TEXT:
///   [KJV] John 3:16 — For God so loved…
///
///   REFERENCE MATERIAL:
///   [S1] Barnes' Notes on the Bible (Evangelical, 19th c.) — John 3:16:
///   …
///
///   ADVENTIST SOURCES:
///   [A1] The Desire of Ages, Chapter 12 — … (Adventist, 19th c.):
///   …
/// </summary>
public static class ContextFormatter
{
    private static readonly Regex BookChapterPattern = new(@", Chapter (\d+) —", RegexOptions.Compiled);

    public static FormattedContext? Format(
        IReadOnlyList<BibleText> verses,
        IReadOnlyList<ScoredChunk> main,
        IReadOnlyList<ScoredChunk> adventist,
        ModuleCatalog catalog,
        int maxChars = int.MaxValue)
    {
        if (verses.Count == 0 && main.Count == 0 && adventist.Count == 0) return null;

        var sources = new List<ContextSource>();
        var mainSb = new StringBuilder();
        var advSb = new StringBuilder();

        if (verses.Count > 0)
        {
            mainSb.AppendLine("VERSE TEXT:");
            foreach (var v in verses)
                mainSb.AppendLine($"[{v.ModuleId}] {v.Reference} — {v.Text}");
        }

        // Budget: drop the lowest-ranked chunks first once the material gets too long.
        var budget = maxChars == int.MaxValue ? maxChars : Math.Max(0, maxChars - mainSb.Length);
        var mainBudget = adventist.Count > 0 && budget != int.MaxValue ? (int)(budget * 0.75) : budget;

        var mainSources = Build(main, "S", catalog, mainBudget);
        if (mainSources.Count > 0)
        {
            if (mainSb.Length > 0) mainSb.AppendLine();
            mainSb.AppendLine("REFERENCE MATERIAL:");
            mainSb.AppendLine();
            Append(mainSb, mainSources);
        }
        sources.AddRange(mainSources);

        var used = mainSources.Sum(s => s.Scored.Chunk.Text.Length + s.Label.Length);
        var advBudget = budget == int.MaxValue ? budget : Math.Max(0, budget - used);
        var advSources = Build(adventist, "A", catalog, advBudget);
        if (advSources.Count > 0)
        {
            advSb.AppendLine("ADVENTIST SOURCES:");
            advSb.AppendLine();
            Append(advSb, advSources);
        }
        sources.AddRange(advSources);

        var mainText = mainSb.Length > 0 ? mainSb.ToString().TrimEnd() : null;
        var advText = advSb.Length > 0 ? advSb.ToString().TrimEnd() : null;
        if (mainText is null && advText is null) return null;

        var text = string.Join("\n\n", new[] { mainText, advText }.Where(t => t is not null));
        return new FormattedContext(text, mainText, advText, sources);
    }

    private static List<ContextSource> Build(
        IReadOnlyList<ScoredChunk> chunks, string prefix, ModuleCatalog catalog, int budgetChars)
    {
        var result = new List<ContextSource>();
        var used = 0;

        foreach (var scored in chunks)
        {
            var c = scored.Chunk;
            var info = catalog.Resolve(c);
            var tradition = c.Tradition ?? info?.Tradition ?? Traditions.Unclassified;
            var display = info?.DisplayName ?? ModuleCatalog.ModuleKey(c);
            var tag = string.IsNullOrEmpty(info?.Era) ? $"({tradition})" : $"({tradition}, {info!.Era})";

            string kind, label;
            int? bookChapterIndex = null;

            if (c.SourceType == SourceType.Book)
            {
                kind = "book";
                // Source already reads "<Title>, Chapter N — <chapter title>".
                label = $"{c.Source} {tag}";
                var m = BookChapterPattern.Match(c.Source);
                if (m.Success) bookChapterIndex = int.Parse(m.Groups[1].Value);
            }
            else
            {
                kind = c.SourceType == SourceType.Dictionary ? "dictionary" : "commentary";
                var where = c.Locator ?? Location(c);
                label = where is null ? $"{display} {tag}" : $"{display} {tag} — {where}";
            }

            var cost = c.Text.Length + label.Length + 16;
            if (used + cost > budgetChars && result.Count > 0) break;
            used += cost;

            result.Add(new ContextSource(
                $"{prefix}{result.Count + 1}", kind, ModuleCatalog.ModuleKey(c), display, tradition, info?.Era,
                label, c.BookNumber, c.ChapterBegin, c.VerseBegin, bookChapterIndex, scored));
        }

        return result;
    }

    private static void Append(StringBuilder sb, List<ContextSource> sources)
    {
        foreach (var s in sources)
        {
            sb.AppendLine($"[{s.Id}] {s.Label}:");
            sb.AppendLine(s.Scored.Chunk.Text);
            sb.AppendLine();
        }
    }

    /// <summary>"John 3:16" / "John 3:16-18" / "John 3" from the chunk's verse metadata.</summary>
    public static string? Location(DocumentChunk c)
    {
        if (c.BookNumber is not int book) return null;
        var name = BibleBookMap.GetFullName(book) ?? $"Book {book}";
        if (c.ChapterBegin is not int ch) return name;
        if (c.VerseBegin is not int vb) return $"{name} {ch}";
        return c.VerseEnd is int ve && ve > vb ? $"{name} {ch}:{vb}-{ve}" : $"{name} {ch}:{vb}";
    }
}
