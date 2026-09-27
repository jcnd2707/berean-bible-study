using System.ComponentModel;
using System.Text;

namespace Berean.Core.Tools;

/// <summary>get_passage — a whole chapter, for the text around a verse.</summary>
public sealed class GetPassageTool(BibleKnowledge knowledge)
{
    public const string Name = "get_passage";

    [Description(
        "Fetches all verses from a Bible chapter to provide surrounding context. " +
        "Call this when you need the text around a verse that is not already in the message. " +
        "Returns the complete chapter text.")]
    public async Task<string> RunAsync(
        [Description("Book name, e.g. 'John', 'Genesis', 'Romans'")] string book,
        [Description("Chapter number, e.g. 3")] int chapter)
    {
        var modules = knowledge.Config.AllowedBibleModules;
        if (modules.Count == 0)
            return "No Bible modules configured for passage lookup.";

        var sb = new StringBuilder();
        var found = 0;

        foreach (var moduleId in modules)
        {
            var chapterRecord = await knowledge.Api.GetBibleChapterAsync(moduleId, book, chapter);
            if (chapterRecord is null) continue;

            sb.AppendLine($"{book} {chapter} ({moduleId}):");
            // Some modules return the same verse more than once.
            foreach (var v in chapterRecord.Verses.GroupBy(v => v.Verse).Select(g => g.First()).OrderBy(v => v.Verse))
                sb.AppendLine($"{v.Reference} {v.Text}");
            sb.AppendLine();
            found++;
        }

        return found > 0
            ? sb.ToString()
            : $"Chapter {book} {chapter} not found in the configured Bible modules.";
    }
}
