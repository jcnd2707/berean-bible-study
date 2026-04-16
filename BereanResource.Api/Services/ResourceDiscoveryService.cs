using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Scans the configured resource folders and returns what is installed.
///
/// Bible modules use the scrollmapper format (.db) with a translations table.
/// Commentary, dictionary, and lexicon modules still use the e-Sword format
/// (.cmtx, .dctx, .lexi/.lexh) with a flat Details table.
/// </summary>
public class ResourceDiscoveryService(IOptions<BereanResourcesConfig> config, ILogger<ResourceDiscoveryService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    public List<ResourceModule> GetBibles() => ScanBibles();
    public List<ResourceModule> GetCommentaries() => ScanESword(_cfg.SubFolders.Commentaries, ".cmt");
    public List<ResourceModule> GetDictionaries() => ScanESword(_cfg.SubFolders.Dictionaries, ".dctx");
    public List<ResourceModule> GetLexicons() => ScanESword(_cfg.SubFolders.Lexicons, ".lexi", ".lexh");
    public List<ResourceModule> GetTopicNotes() => ScanESword(_cfg.SubFolders.TopicNotes, ".topx");

    public string? ResolvePath(string subFolder, string moduleId, params string[] extensions)
    {
        var folder = Path.Combine(_cfg.RootPath, subFolder);
        foreach (var ext in extensions)
        {
            var path = Path.Combine(folder, moduleId + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ── Bibles (scrollmapper .db format) ─────────────────────────────────────

    private List<ResourceModule> ScanBibles()
    {
        var folder = Path.Combine(_cfg.RootPath, _cfg.SubFolders.Bibles);
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("Bibles folder not found: {Folder}", folder);
            return new List<ResourceModule>();
        }

        var results = new List<ResourceModule>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.db"))
        {
            var moduleId = Path.GetFileNameWithoutExtension(file);
            var (name, language) = ReadScrollmapperMetadata(file, moduleId);
            results.Add(new ResourceModule(moduleId, name, language, file));
        }

        return results.OrderBy(r => r.Language).ThenBy(r => r.Name).ToList();
    }

    /// <summary>
    /// Reads name and language from the scrollmapper translations table.
    /// Schema: translation (TEXT), title (TEXT), license (TEXT)
    /// </summary>
    private (string Name, string Language) ReadScrollmapperMetadata(string filePath, string fallback)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT translation, title FROM translations LIMIT 1";
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return (fallback, "en");

            var abbreviation = reader.IsDBNull(0) ? fallback : reader.GetString(0).Trim();
            var rawTitle = reader.IsDBNull(1) ? abbreviation : reader.GetString(1).Trim();

            // titles are stored as "# KJV: King James Version ..." — strip the markdown prefix
            var title = rawTitle.TrimStart('#').Trim();
            if (title.Contains(':'))
                title = title[(title.IndexOf(':') + 1)..].Trim();

            // Infer language from the abbreviation prefix (Spa=es, Por=pt, Ger=de, etc.)
            var language = InferLanguageFromAbbreviation(abbreviation);

            return (title, language);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read scrollmapper metadata from {File}", filePath);
            return (fallback, "en");
        }
    }

    // ── e-Sword modules (commentaries, dictionaries, lexicons) ────────────────

    private List<ResourceModule> ScanESword(string subFolder, params string[] extensions)
    {
        var folder = Path.Combine(_cfg.RootPath, subFolder);
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("Resource folder not found: {Folder}", folder);
            return new List<ResourceModule>();
        }

        var results = new List<ResourceModule>();

        foreach (var ext in extensions)
        {
            foreach (var file in Directory.EnumerateFiles(folder, $"*{ext}"))
            {
                var moduleId = Path.GetFileNameWithoutExtension(file);
                var (name, language) = ReadESwordMetadata(file, moduleId);
                results.Add(new ResourceModule(moduleId, name, language, file));
            }
        }

        return results.OrderBy(r => r.Language).ThenBy(r => r.Name).ToList();
    }

    /// <summary>
    /// Reads the flat Details table used by e-Sword commentary/dictionary files.
    /// The real column names are discovered via PRAGMA because they vary by module author:
    ///   Title, Abbreviation, Information, Version, OldTestament, NewTestament, ...
    /// </summary>
    private (string Name, string Language) ReadESwordMetadata(string filePath, string fallback)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly");
            conn.Open();

            var columns = GetTableColumns(conn, "Details");
            if (columns.Count == 0)
            {
                logger.LogWarning("No Details table in {File}", filePath);
                return (fallback, "en");
            }

            // Read the single-row Details table into a dictionary keyed by column name
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Details LIMIT 1";
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return (fallback, "en");

            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var col = reader.GetName(i);
                var val = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i)?.ToString() ?? string.Empty;
                details[col] = val.Trim();
            }

            var name = details.GetValueOrDefault("Title")
                    ?? details.GetValueOrDefault("Description")
                    ?? details.GetValueOrDefault("Abbreviation")
                    ?? fallback;

            var rawLang = details.GetValueOrDefault("Language") ?? "en";
            var language = NormaliseLanguage(rawLang);

            return (name, language);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read e-Sword metadata from {File}", filePath);
            return (fallback, "en");
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static List<string> GetTableColumns(SqliteConnection conn, string tableName)
    {
        var columns = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{tableName}])";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>
    /// Infers ISO 639-1 language code from a scrollmapper translation abbreviation.
    /// Scrollmapper uses language prefixes: Spa=es, Por=pt, Ger=de, etc.
    /// </summary>
    private static string InferLanguageFromAbbreviation(string abbr)
    {
        if (string.IsNullOrWhiteSpace(abbr)) return "en";

        return abbr[..Math.Min(3, abbr.Length)].ToLowerInvariant() switch
        {
            "spa" => "es",
            "por" => "pt",
            "ger" or "deu" => "de",
            "fre" or "fre" => "fr",
            "ita" => "it",
            "rus" => "ru",
            "chi" => "zh",
            "kor" => "ko",
            "jap" => "ja",
            "heb" or "wlc" or "map" => "he",
            "grk" or "grc" or "byz" or "tr " or "sta" => "el",
            "lat" or "vul" => "la",
            "nor" or "nor" => "no",
            "swe" => "sv",
            "fin" => "fi",
            "pol" => "pl",
            "cze" => "cs",
            "dut" or "nl " => "nl",
            "ukr" => "uk",
            "arm" => "hy",
            "ara" => "ar",
            _ => "en"
        };
    }

    private static string NormaliseLanguage(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "english" or "eng" => "en",
            "spanish" or "español" or "espanol" or "spa" => "es",
            "portuguese" or "português" or "por" => "pt",
            "french" or "français" or "francais" or "fra" => "fr",
            "german" or "deutsch" or "deu" => "de",
            "italian" or "italiano" or "ita" => "it",
            _ => raw.Length >= 2 ? raw[..2].ToLowerInvariant() : raw.ToLowerInvariant()
        };
    }
}