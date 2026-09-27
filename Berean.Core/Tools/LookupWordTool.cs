using System.ComponentModel;
using System.Text;

namespace Berean.Core.Tools;

/// <summary>lookup_word — a word, name or Strong's number in the dictionaries and lexicons.</summary>
public sealed class LookupWordTool(BibleKnowledge knowledge)
{
    public const string Name = "lookup_word";
    private const int MaxDefinitionChars = 1000;

    [Description(
        "Looks up a word, name, or theological term in the biblical dictionaries and lexicons. " +
        "Use this when the user asks about the meaning of a Greek or Hebrew word, a theological " +
        "concept, a biblical name, or a Strong's number (e.g. G25, H430). " +
        "Returns the definition from all available dictionaries.")]
    public async Task<string> RunAsync(
        [Description("Word, term, name, or Strong's number to look up (e.g. 'agape', 'pneuma', 'G25', 'hesed')")] string word)
    {
        var hits = await DictionaryLookup.FindAsync(knowledge.Api, knowledge.Rag.Catalog.Dictionaries, word.Trim());
        if (hits.Count == 0)
            return $"No definition found for '{word}' in the available dictionaries.";

        var sb = new StringBuilder();
        foreach (var hit in hits)
        {
            var def = hit.Definition.Length > MaxDefinitionChars ? hit.Definition[..MaxDefinitionChars] + "…" : hit.Definition;
            sb.AppendLine($"**{hit.Topic}** ({hit.Module.DisplayName} {hit.Module.TraditionTag}):");
            sb.AppendLine(def);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
