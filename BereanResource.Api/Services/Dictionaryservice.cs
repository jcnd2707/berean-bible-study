using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

public class DictionaryService(
    ResourceDiscoveryService discovery,
    IOptions<BereanResourcesConfig> config,
    ILogger<DictionaryService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    /// <summary>Looks up a dictionary entry by word or topic key.</summary>
    public DictionaryEntry? LookupByWord(string moduleId, string word)
    {
        var (path, isDictionary) = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        // Both .dctx and .lexi/.lexh use a Topic/Definition pattern
        // but the table name differs: Dictionary vs Lexicon
        var table = isDictionary ? "Dictionary" : "Lexicon";

        cmd.CommandText = $"""
            SELECT Topic, Definition
            FROM   {table}
            WHERE  Topic = $word
            """;
        cmd.Parameters.AddWithValue("$word", word);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new DictionaryEntry(
            Topic: reader.GetString(0),
            Definition: reader.IsDBNull(1) ? string.Empty : StripHtml(reader.GetString(1))
        );
    }

    /// <summary>
    /// Looks up by Strong's number. e-Sword lexicons key entries like "H1" or "G5485".
    /// </summary>
    public DictionaryEntry? LookupByStrongs(string moduleId, string strongsNumber)
    {
        // Normalise: accept "H001", "H1", "G5485" etc.
        var normalised = NormaliseStrongs(strongsNumber);
        return LookupByWord(moduleId, normalised);
    }

    /// <summary>Full-text search across topics — useful for the UI search box.</summary>
    public List<DictionaryEntry> Search(string moduleId, string query, int limit = 20)
    {
        var (path, isDictionary) = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        var table = isDictionary ? "Dictionary" : "Lexicon";

        cmd.CommandText = $"""
            SELECT Topic, Definition
            FROM   {table}
            WHERE  Topic LIKE $query
            ORDER  BY Topic
            LIMIT  $limit
            """;
        cmd.Parameters.AddWithValue("$query", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();

        var results = new List<DictionaryEntry>();
        while (reader.Read())
        {
            results.Add(new DictionaryEntry(
                Topic: reader.GetString(0),
                Definition: reader.IsDBNull(1) ? string.Empty : StripHtml(reader.GetString(1))
            ));
        }

        return results;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (string Path, bool IsDictionary) ResolveOrThrow(string moduleId)
    {
        // Try dictionary first, then lexicons
        var path = discovery.ResolvePath(_cfg.SubFolders.Dictionaries, moduleId, ".dctx");
        if (path is not null) return (path, true);

        path = discovery.ResolvePath(_cfg.SubFolders.Lexicons, moduleId, ".lexi", ".lexh");
        if (path is not null) return (path, false);

        throw new FileNotFoundException($"Dictionary/lexicon module '{moduleId}' not found.");
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    private static string NormaliseStrongs(string input)
    {
        // Strip leading zeros from the numeric part: "H001" → "H1", "G05485" → "G5485"
        if (input.Length < 2) return input.ToUpperInvariant();
        var prefix = char.ToUpper(input[0]);
        var number = input[1..].TrimStart('0');
        return $"{prefix}{number}";
    }

    private static string StripHtml(string text)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
        return result.Trim();
    }
}