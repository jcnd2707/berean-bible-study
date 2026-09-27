using System.ComponentModel;
using System.Text;

namespace Berean.Core.Tools;

/// <summary>lookup_verse — the exact text of one verse from the configured Bible modules.</summary>
public sealed class LookupVerseTool(BibleKnowledge knowledge)
{
    public const string Name = "lookup_verse";

    [Description(
        "Fetches the text of a specific Bible verse from the configured Bible translation(s). " +
        "Provide the book name, chapter number, and verse number.")]
    public async Task<string> RunAsync(
        [Description("Book name, e.g. 'John', 'Genesis', 'Romans'")] string book,
        [Description("Chapter number, e.g. 3")] int chapter,
        [Description("Verse number, e.g. 16")] int verse)
    {
        var modules = knowledge.Config.AllowedBibleModules;
        if (modules.Count == 0)
            return "No Bible modules configured for verse lookup. Check AllowedBibleModules in appsettings.json.";

        var sb = new StringBuilder();
        var found = 0;

        foreach (var moduleId in modules)
        {
            var chapterRecord = await knowledge.Api.GetBibleChapterAsync(moduleId, book, chapter);
            var verseRecord = chapterRecord?.Verses.FirstOrDefault(v => v.Verse == verse);
            if (verseRecord is null) continue;

            sb.AppendLine($"**{verseRecord.Reference}** ({moduleId}):");
            sb.AppendLine(verseRecord.Text);
            sb.AppendLine();
            found++;
        }

        return found > 0
            ? sb.ToString()
            : $"Verse {book} {chapter}:{verse} not found in the configured Bible modules.";
    }
}
