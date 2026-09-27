using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BereanResourceApi.Services;

/// <summary>
/// Supports two on-disk formats:
///
///   MySword  (.dct)        — table: dictionary   columns: [relativeorder?,] word, data
///   e-Sword  (.lexi/.lexh) — table: Lexicon       columns: Topic, Definition
///
/// Within MySword .dct files the <c>relativeorder</c> column is optional:
/// Strong's-based modules (strong.dct, bdb.dct) include it; some plain-word
/// dictionaries (eastons.dct) do not. The service detects this at open-time
/// via PRAGMA table_info and falls back to ordering by <c>word</c>.
/// </summary>
public class DictionaryService(
    ResourceDiscoveryService discovery,
    IOptions<BereanResourcesConfig> config,
    ILogger<DictionaryService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    // Per-module index of "Transliteration: nephesh" → entries, built on first use.
    private readonly ConcurrentDictionary<string, Lazy<Dictionary<string, List<DictionaryEntry>>>> _translit
        = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TransliterationField = new(
        @"Transliteration\s*:\s*(?<t>[^\s,;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Looks up an entry by its exact key (word / topic).</summary>
    public DictionaryEntry? LookupByWord(string moduleId, string word)
    {
        var info = ResolveOrThrow(moduleId);
        using var conn = Open(info.Path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = info.Format == ModuleFormat.MySword
            ? "SELECT word, data FROM dictionary WHERE word = $word COLLATE NOCASE"
            : "SELECT Topic, Definition FROM Lexicon WHERE Topic = $word";

        cmd.Parameters.AddWithValue("$word", word);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    /// <summary>
    /// Looks up by Strong's number.
    /// Accepts any padding style: "H001", "H1", "G05485", "G5485".
    /// </summary>
    public DictionaryEntry? LookupByStrongs(string moduleId, string strongsNumber)
        => LookupByWord(moduleId, NormaliseStrongs(strongsNumber));

    /// <summary>
    /// Returns metadata stored in the <c>details</c> table (MySword .dct only).
    /// Returns <c>null</c> for e-Sword modules that have no such table.
    /// </summary>
    public ModuleDetails? GetDetails(string moduleId)
    {
        var info = ResolveOrThrow(moduleId);
        if (info.Format != ModuleFormat.MySword) return null;

        using var conn = Open(info.Path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            SELECT title, abbreviation, description, author, version, versiondate,
                   publisher, strong, righttoleft
            FROM   details
            LIMIT  1
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ModuleDetails(
            Title: reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
            Abbreviation: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            Description: reader.IsDBNull(2) ? string.Empty : StripHtml(reader.GetString(2)),
            Author: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Version: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            VersionDate: reader.IsDBNull(5) ? null : reader.GetString(5),
            Publisher: reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            IsStrongs: !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
            RightToLeft: !reader.IsDBNull(8) && reader.GetInt32(8) != 0
        );
    }

    /// <summary>Full-text search across word/topic keys.</summary>
    public List<DictionaryEntry> Search(string moduleId, string query, int limit = 20)
    {
        var info = ResolveOrThrow(moduleId);
        using var conn = Open(info.Path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = info.Format == ModuleFormat.MySword
            ? $"""
               SELECT word, data
               FROM   dictionary
               WHERE  word LIKE $query
               ORDER  BY {(info.HasRelativeOrder ? "relativeorder" : "word")}
               LIMIT  $limit
               """
            : """
              SELECT Topic, Definition
              FROM   Lexicon
              WHERE  Topic LIKE $query
              ORDER  BY Topic
              LIMIT  $limit
              """;

        cmd.Parameters.AddWithValue("$query", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        var results = new List<DictionaryEntry>();
        while (reader.Read())
            results.Add(ReadEntry(reader));

        return results;
    }

    /// <summary>
    /// Finds Strong's-style entries by the transliteration in their definition, ignoring case
    /// and diacritics: "agape" finds the entry whose text says "Transliteration: agapē".
    /// Strong's and BDB are keyed by number (H5315), so this is how a word like "nephesh"
    /// gets from the user's question to its entry.
    /// </summary>
    public List<DictionaryEntry> FindByTransliteration(string moduleId, string term, int limit = 5)
    {
        var index = _translit.GetOrAdd(moduleId, id => new Lazy<Dictionary<string, List<DictionaryEntry>>>(
            () => BuildTransliterationIndex(id))).Value;

        return index.TryGetValue(FoldForMatch(term), out var entries)
            ? entries.Take(limit).ToList()
            : [];
    }

    private Dictionary<string, List<DictionaryEntry>> BuildTransliterationIndex(string moduleId)
    {
        var info = ResolveOrThrow(moduleId);
        var index = new Dictionary<string, List<DictionaryEntry>>();

        using var conn = Open(info.Path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = info.Format == ModuleFormat.MySword
            ? "SELECT word, data FROM dictionary"
            : "SELECT Topic, Definition FROM Lexicon";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = ReadEntry(reader);
            var m = TransliterationField.Match(entry.Definition);
            if (!m.Success) continue;

            var key = FoldForMatch(m.Groups["t"].Value);
            if (key.Length == 0) continue;
            if (!index.TryGetValue(key, out var list)) index[key] = list = [];
            list.Add(entry);
        }

        logger.LogInformation("Built transliteration index for '{ModuleId}': {Count} keys", moduleId, index.Count);
        return index;
    }

    /// <summary>Lower-case letters only, diacritics removed ("agapē" → "agape").</summary>
    private static string FoldForMatch(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetter(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// Returns a page of entries in their natural order.
    /// MySword modules order by <c>relativeorder</c> when available, else by <c>word</c>.
    /// </summary>
    public List<DictionaryEntry> GetPage(string moduleId, int offset = 0, int pageSize = 50)
    {
        var info = ResolveOrThrow(moduleId);
        using var conn = Open(info.Path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = info.Format == ModuleFormat.MySword
            ? $"""
               SELECT word, data
               FROM   dictionary
               ORDER  BY {(info.HasRelativeOrder ? "relativeorder" : "word")}
               LIMIT  $limit OFFSET $offset
               """
            : """
              SELECT Topic, Definition
              FROM   Lexicon
              ORDER  BY Topic
              LIMIT  $limit OFFSET $offset
              """;

        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", offset);

        using var reader = cmd.ExecuteReader();
        var results = new List<DictionaryEntry>();
        while (reader.Read())
            results.Add(ReadEntry(reader));

        return results;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private enum ModuleFormat { MySword, ESword }

    /// <param name="Path">Absolute path to the module file.</param>
    /// <param name="Format">File format (MySword .dct vs e-Sword .lexi/.lexh).</param>
    /// <param name="HasRelativeOrder">
    ///   True when the <c>dictionary</c> table contains a <c>relativeorder</c> column.
    ///   Most MySword .dct files include it, but some plain-word dictionaries (e.g.
    ///   Easton's) were published without it.
    /// </param>
    private record ModuleInfo(string Path, ModuleFormat Format, bool HasRelativeOrder);

    private ModuleInfo ResolveOrThrow(string moduleId)
    {
        var path = discovery.ResolvePath(_cfg.SubFolders.Dictionaries, moduleId, ".dct");
        if (path is not null)
        {
            var hasOrder = DctHasRelativeOrder(path);
            logger.LogInformation(
                "Resolved '{ModuleId}' → MySword .dct at {Path} (relativeorder={HasOrder})",
                moduleId, path, hasOrder);
            return new ModuleInfo(path, ModuleFormat.MySword, hasOrder);
        }

        path = discovery.ResolvePath(_cfg.SubFolders.Lexicons, moduleId, ".lexi", ".lexh");
        if (path is not null)
        {
            logger.LogInformation("Resolved '{ModuleId}' → e-Sword lexicon at {Path}", moduleId, path);
            return new ModuleInfo(path, ModuleFormat.ESword, false);
        }

        throw new FileNotFoundException($"Dictionary/lexicon module '{moduleId}' not found.");
    }

    /// <summary>
    /// Checks whether the <c>dictionary</c> table actually has a <c>relativeorder</c>
    /// column. This varies across MySword module publishers.
    /// </summary>
    private static bool DctHasRelativeOrder(string path)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(dictionary)";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals("relativeorder", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Column positions are identical for both formats: 0 = key, 1 = content.
    private static DictionaryEntry ReadEntry(SqliteDataReader reader)
    {
        var topic = reader.GetString(0);
        var rawContent = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        return new DictionaryEntry(Topic: topic, Definition: StripHtml(rawContent));
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Normalises a Strong's number: prefix uppercased, no leading zeros.
    /// "H001" → "H1", "g05485" → "G5485", "H000" → "H0".
    /// </summary>
    private static string NormaliseStrongs(string input)
    {
        if (input.Length < 2) return input.ToUpperInvariant();
        var prefix = char.ToUpper(input[0]);
        var number = input[1..].TrimStart('0');
        if (number.Length == 0) number = "0";
        return $"{prefix}{number}";
    }

    private static string StripHtml(string text)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
        return result.Trim();
    }
}