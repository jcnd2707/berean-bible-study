using System.ComponentModel;
using System.Text;

namespace Berean.Core.Tools;

/// <summary>get_cross_references — the passages most closely linked to a verse, with their text.</summary>
public sealed class CrossReferencesTool(BibleKnowledge knowledge)
{
    public const string Name = "get_cross_references";
    private const int MaxReferences = 10;
    private const int MaxTextChars = 300;

    [Description(
        "Returns the passages most closely cross-referenced with a Bible verse, with their text. " +
        "ALWAYS provide the 'verse' parameter — e.g. 'John 3:16'. " +
        "Do not call this tool without a verse.")]
    public async Task<string> RunAsync(
        [Description("Verse reference, e.g. 'John 3:16' or 'Romans 8:28'")] string verse)
    {
        var r = QueryRouter.TryParseVerse(verse);
        if (r?.Verse is null)
            return $"Could not read a verse reference from '{verse}'. Use a form like 'John 3:16'.";

        var bookName = BibleBookMap.GetFullName(r.BookNumber) ?? verse;
        var result = await knowledge.Api.GetCrossReferencesAsync(bookName, r.Chapter, r.Verse.Value, MaxReferences);
        if (result is null || result.References.Count == 0)
            return $"No cross-references found for {bookName} {r.Chapter}:{r.Verse}.";

        var module = knowledge.Config.AllowedBibleModules.FirstOrDefault();
        var chapters = new Dictionary<(int book, int chapter), ApiChapterRecord?>();
        var sb = new StringBuilder();
        sb.AppendLine($"Cross-references for {result.Reference}, strongest first:");

        foreach (var x in result.References)
        {
            var text = module is null ? null : await VerseTextAsync(module, x, chapters);
            sb.AppendLine(text is null ? $"- {x.ToReference}" : $"- {x.ToReference}: {text}");
        }
        return sb.ToString();
    }

    /// <summary>The text of a cross-referenced passage from one Bible module (each chapter is fetched once).</summary>
    private async Task<string?> VerseTextAsync(
        string moduleId, ApiCrossReferenceEntry x, Dictionary<(int book, int chapter), ApiChapterRecord?> chapters)
    {
        var name = BibleBookMap.GetFullName(x.ToBook);
        if (name is null) return null;

        if (!chapters.TryGetValue((x.ToBook, x.ToChapter), out var chapter))
            chapters[(x.ToBook, x.ToChapter)] = chapter = await knowledge.Api.GetBibleChapterAsync(moduleId, name, x.ToChapter);
        if (chapter is null) return null;

        var end = Math.Max(x.ToVerseStart, x.ToVerseEnd);
        var text = string.Join(" ", chapter.Verses
            .GroupBy(v => v.Verse).Select(g => g.First())
            .Where(v => v.Verse >= x.ToVerseStart && v.Verse <= end)
            .OrderBy(v => v.Verse).Select(v => v.Text.Trim()));

        if (text.Length == 0) return null;
        return text.Length > MaxTextChars ? text[..MaxTextChars] + "…" : text;
    }
}
