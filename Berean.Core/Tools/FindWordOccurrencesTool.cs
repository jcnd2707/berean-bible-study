using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Berean.Core.Tools;

/// <summary>
/// find_word_occurrences — how a Strong's number is used across the Bible: the count, the spread
/// by book and a sample of verses. What "how is G26 used across the NT" needs.
/// </summary>
public sealed class FindWordOccurrencesTool(BibleKnowledge knowledge)
{
    public const string Name = "find_word_occurrences";
    private static readonly Regex StrongsNumber = new(@"^[GH]\d{1,5}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Description(
        "Shows where a Greek or Hebrew word is used in the Bible, by its Strong's number (e.g. G26 for agape, " +
        "H2617 for hesed): how many times, in which books, and a sample of verses with their text. " +
        "Look the word up with lookup_word first if you only know the transliteration.")]
    public async Task<string> RunAsync(
        [Description("Strong's number, e.g. 'G26' or 'H2617'")] string strongs,
        [Description("How many sample verses to include (default 15, at most 40)")] int limit = 15)
    {
        strongs = strongs.Trim();
        if (!StrongsNumber.IsMatch(strongs))
            return $"'{strongs}' is not a Strong's number. Use a form like G26 or H2617.";

        limit = Math.Clamp(limit, 1, 40);

        // The first configured Bible module that carries Strong's tags answers.
        foreach (var module in knowledge.Config.AllowedBibleModules)
        {
            var result = await knowledge.Api.GetStrongsOccurrencesAsync(module, strongs, limit);
            if (result is null) continue;
            if (result.Count == 0) return $"{result.Number} does not occur in {module}.";

            var sb = new StringBuilder();
            sb.AppendLine($"{result.Number} occurs {result.Count} time(s) in {module}.");
            sb.AppendLine("By book: " + string.Join(", ", result.ByBook.Select(b => $"{b.BookName} {b.Count}")));
            sb.AppendLine();
            sb.AppendLine("Sample verses (the word as translated in quotes):");
            foreach (var s in result.Sample)
                sb.AppendLine($"- {s.Reference} (\"{s.Word.Trim(' ', ',')}\"): {s.Text}");
            return sb.ToString();
        }

        return "None of the configured Bible modules carries Strong's numbers.";
    }
}
