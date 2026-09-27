namespace Berean.Core.Retrieval;

public sealed record DictionaryHit(ModuleInfo Module, string Topic, string Definition);

/// <summary>
/// Finds a word in the dictionaries and lexicons. Shared by the router (which looks words up
/// before the model runs) and the lookup_word tool (for models that call tools).
/// </summary>
public static class DictionaryLookup
{
    public static bool IsStrongsNumber(string term) =>
        term.Length >= 2 && term[0] is 'G' or 'H' or 'g' or 'h' && term[1..].All(char.IsDigit);

    /// <summary>
    /// The entry for <paramref name="term"/> in each module that has one. Tries, in order:
    /// the exact headword or Strong's number; the transliteration inside the entry text (Strong's
    /// and BDB are keyed by number, so "nephesh" is found this way); a headword search whose
    /// result really contains the term.
    /// </summary>
    public static async Task<List<DictionaryHit>> FindAsync(
        BereanResourceApiClient api, IEnumerable<ModuleInfo> modules, string term, CancellationToken ct = default)
    {
        var found = await Task.WhenAll(modules.Select(async module =>
        {
            var entry = await api.LookupWordAsync(module.ModuleId, term, ct);

            if (entry is null && !IsStrongsNumber(term))
                entry = (await api.FindByTransliterationAsync(module.ModuleId, term, limit: 1, ct)).FirstOrDefault();

            if (entry is null && !IsStrongsNumber(term))
            {
                var hits = await api.SearchDictionaryAsync(module.ModuleId, term, limit: 3, ct);
                entry = hits.FirstOrDefault(h => h.Topic.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            return entry is null || string.IsNullOrWhiteSpace(entry.Definition)
                ? null
                : new DictionaryHit(module, entry.Topic, entry.Definition);
        }));

        return found.Where(h => h is not null).Select(h => h!).ToList();
    }
}
