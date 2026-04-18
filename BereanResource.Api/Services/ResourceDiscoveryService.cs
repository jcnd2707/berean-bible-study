using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BereanResourceApi.Services;

// ── Group manifest types ──────────────────────────────────────────────────────

/// <summary>
/// Describes one logical commentary that is split across multiple volume files.
/// Add entries to commentary-groups.json for commentaries that cannot be
/// auto-detected (e.g., volumes whose titles don't share a common stem).
/// </summary>
public sealed record CommentaryGroupEntry(
    /// <summary>The logical moduleId callers use in API requests.</summary>
    string GroupId,
    /// <summary>Human-readable title for the whole commentary.</summary>
    string Title,
    /// <summary>Short abbreviation for the whole commentary.</summary>
    string Abbreviation,
    /// <summary>
    /// Module IDs of the individual volume files, in book-number order.
    /// These must match the file names (without extension) on disk.
    /// </summary>
    IReadOnlyList<string> VolumeModuleIds
);

// ── Service ───────────────────────────────────────────────────────────────────

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

    // Lazy-built group registry: groupId -> ordered list of volume paths.
    // Built once on first use and reused for the lifetime of the service.
    private Dictionary<string, IReadOnlyList<string>>? _groupRegistry;
    private readonly object _registryLock = new();

    // ── Public scanning API ───────────────────────────────────────────────────

    public List<ResourceModule> GetBibles() => ScanBibles();
    public List<ResourceModule> GetDictionaries() => ScanESword(_cfg.SubFolders.Dictionaries, ".dct");
    public List<ResourceModule> GetLexicons() => ScanESword(_cfg.SubFolders.Lexicons, ".lexi", ".lexh");
    public List<ResourceModule> GetTopicNotes() => ScanESword(_cfg.SubFolders.TopicNotes, ".topx");

    /// <summary>
    /// Returns all commentaries with multi-volume sets collapsed into a single
    /// entry under their logical groupId. Individual volume files that belong
    /// to a group are excluded from the list.
    /// </summary>
    public List<ResourceModule> GetCommentaries()
    {
        var folder = Path.Combine(_cfg.RootPath, _cfg.SubFolders.Commentaries);
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("Resource folder not found: {Folder}", folder);
            return [];
        }

        var registry = EnsureGroupRegistry();

        // Collect file paths already claimed by a group so we can skip them.
        var claimedPaths = new HashSet<string>(
            registry.Values.SelectMany(v => v),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<ResourceModule>();

        // 1. One entry per group, using info read from the first volume.
        foreach (var (groupId, volumePaths) in registry)
        {
            var firstPath = volumePaths.FirstOrDefault();
            if (firstPath is null) continue;

            var (name, language) = ReadESwordMetadata(firstPath, groupId);

            // Strip the volume marker from the name so it reads as the full series.
            name = DeriveGroupTitle(name);

            results.Add(new ResourceModule(groupId, name, language, firstPath));
        }

        // 2. Single-file commentaries that aren't part of any group.
        foreach (var file in Directory.EnumerateFiles(folder, "*.cmt"))
        {
            if (claimedPaths.Contains(file)) continue;

            var moduleId = Path.GetFileNameWithoutExtension(file);
            var (name, language) = ReadESwordMetadata(file, moduleId);
            results.Add(new ResourceModule(moduleId, name, language, file));
        }

        return results.OrderBy(r => r.Language).ThenBy(r => r.Name).ToList();
    }

    // ── Commentary group registry ─────────────────────────────────────────────

    /// <summary>
    /// Returns the group registry: groupId -> ordered list of resolved volume paths.
    /// Used by <see cref="CommentaryService"/> to route requests to the right volume.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetCommentaryGroupRegistry()
        => EnsureGroupRegistry();

    private Dictionary<string, IReadOnlyList<string>> EnsureGroupRegistry()
    {
        if (_groupRegistry is not null) return _groupRegistry;

        lock (_registryLock)
        {
            if (_groupRegistry is not null) return _groupRegistry;
            _groupRegistry = BuildGroupRegistry();
        }

        return _groupRegistry;
    }

    private Dictionary<string, IReadOnlyList<string>> BuildGroupRegistry()
    {
        var registry = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);

        // 1. Explicit manifest entries take priority.
        foreach (var entry in LoadManifest())
        {
            var paths = new List<string>();
            foreach (var volId in entry.VolumeModuleIds)
            {
                var p = ResolvePath(_cfg.SubFolders.Commentaries, volId, ".cmt");
                if (p is null)
                {
                    logger.LogWarning(
                        "Group '{GroupId}': volume '{VolId}' not found on disk — skipping.",
                        entry.GroupId, volId);
                    continue;
                }
                paths.Add(p);
            }

            if (paths.Count > 1)
                registry[entry.GroupId] = paths;
            else
                logger.LogWarning(
                    "Group '{GroupId}' has fewer than 2 resolvable volumes — ignoring.",
                    entry.GroupId);
        }

        // 2. Auto-detect: scan all .cmt files, group by derived title stem.
        var folder = Path.Combine(_cfg.RootPath, _cfg.SubFolders.Commentaries);
        if (!Directory.Exists(folder)) return registry;

        // moduleId -> (derivedStem, filePath)
        var stemMap = new Dictionary<string, (string stem, string path)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(folder, "*.cmt"))
        {
            // Skip volumes already claimed by a manifest entry.
            if (registry.Values.Any(v => v.Contains(file, StringComparer.OrdinalIgnoreCase)))
                continue;

            var (name, _) = ReadESwordMetadata(file, string.Empty);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var stem = DeriveGroupTitle(name);
            var modId = Path.GetFileNameWithoutExtension(file);
            stemMap[modId] = (stem, file);
        }

        // Group by stem; only form a group when 2+ volumes share one.
        foreach (var g in stemMap.GroupBy(kv => kv.Value.stem, StringComparer.OrdinalIgnoreCase))
        {
            var members = g.ToList();
            if (members.Count < 2) continue;

            var groupId = SlugFromTitle(g.Key);

            // Skip if already claimed by a manifest entry.
            if (registry.ContainsKey(groupId)) continue;

            // Sort volumes by their lowest book number so order is deterministic.
            var ordered = members
                .OrderBy(kv => GetMinCommentaryBook(kv.Value.path))
                .Select(kv => kv.Value.path)
                .ToList();

            registry[groupId] = ordered;
            logger.LogInformation(
                "Auto-grouped commentary '{GroupId}' from {Count} volumes.",
                groupId, ordered.Count);
        }

        return registry;
    }

    // ── Manifest loading ──────────────────────────────────────────────────────

    private List<CommentaryGroupEntry> LoadManifest()
    {
        var commentaryRoot = ResolveFolderPath(_cfg.SubFolders.Commentaries);
        var candidates = new[]
        {
            commentaryRoot is not null
                ? Path.Combine(commentaryRoot, "commentary-groups.json")
                : null,
            Path.Combine(AppContext.BaseDirectory, "commentary-groups.json"),
        };

        foreach (var candidate in candidates)
        {
            if (candidate is null || !File.Exists(candidate)) continue;

            try
            {
                var json = File.ReadAllText(candidate);
                var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("groups", out var groupsEl))
                    return [];

                var entries = new List<CommentaryGroupEntry>();
                foreach (var g in groupsEl.EnumerateArray())
                {
                    var groupId = g.GetProperty("groupId").GetString() ?? string.Empty;
                    var title = g.GetProperty("title").GetString() ?? groupId;
                    var abbr = g.TryGetProperty("abbreviation", out var a)
                                  ? a.GetString() ?? groupId : groupId;
                    var volIds = g.GetProperty("volumeModuleIds")
                                   .EnumerateArray()
                                   .Select(v => v.GetString() ?? string.Empty)
                                   .Where(v => v.Length > 0)
                                   .ToList();

                    if (groupId.Length > 0 && volIds.Count > 0)
                        entries.Add(new CommentaryGroupEntry(groupId, title, abbr, volIds));
                }

                return entries;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to parse commentary-groups.json at '{Path}'.", candidate);
            }
        }

        return [];
    }

    // ── Path helpers ──────────────────────────────────────────────────────────

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

    public string? ResolveFolderPath(string subFolder)
    {
        var root = Path.Combine(_cfg.RootPath, subFolder);
        return Directory.Exists(root) ? root : null;
    }

    public IEnumerable<string> ListModuleIds(string subFolder, string extension)
    {
        var root = ResolveFolderPath(subFolder);
        if (root is null) return [];

        return Directory
            .EnumerateFiles(root, $"*{extension}", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileNameWithoutExtension(f));
    }

    // ── Bibles (scrollmapper .db format) ─────────────────────────────────────

    private List<ResourceModule> ScanBibles()
    {
        var folder = Path.Combine(_cfg.RootPath, _cfg.SubFolders.Bibles);
        if (!Directory.Exists(folder))
        {
            logger.LogWarning("Bibles folder not found: {Folder}", folder);
            return [];
        }

        var results = new List<ResourceModule>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.db")
                             .Concat(Directory.EnumerateFiles(folder, "*.bbl")))
        {
            var moduleId = Path.GetFileNameWithoutExtension(file);
            var (name, language) = ReadScrollmapperMetadata(file, moduleId);
            results.Add(new ResourceModule(moduleId, name, language, file));
        }

        return results.OrderBy(r => r.Language).ThenBy(r => r.Name).ToList();
    }

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

            var title = rawTitle.TrimStart('#').Trim();
            if (title.Contains(':'))
                title = title[(title.IndexOf(':') + 1)..].Trim();

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
            return [];
        }

        var results = new List<ResourceModule>();

        foreach (var ext in extensions)
            foreach (var file in Directory.EnumerateFiles(folder, $"*{ext}"))
            {
                var moduleId = Path.GetFileNameWithoutExtension(file);
                var (name, language) = ReadESwordMetadata(file, moduleId);
                results.Add(new ResourceModule(moduleId, name, language, file));
            }

        return results.OrderBy(r => r.Language).ThenBy(r => r.Name).ToList();
    }

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

    // ── Commentary-specific DB helpers ────────────────────────────────────────

    private static int GetMinCommentaryBook(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(book) FROM commentary";
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? int.MaxValue : Convert.ToInt32(result);
    }

    // ── Title / slug helpers ──────────────────────────────────────────────────

    // Matches common volume markers:  ", vol. 3"  "Volume II"  "Part 1"  "v2"
    private static readonly Regex VolumePattern = new(
        @",?\s*(vol\.?\s*\d+|volume\s*\d+|part\s*\d+|\bv\d+\b)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string DeriveGroupTitle(string rawTitle)
        => VolumePattern.Replace(rawTitle, " ").Trim();

    internal static string DeriveGroupAbbreviation(string rawAbbr)
        => VolumePattern.Replace(rawAbbr, "").Trim().TrimEnd('(', '-', '_', ' ');

    private static string SlugFromTitle(string title)
        => Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');

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

    private static string InferLanguageFromAbbreviation(string abbr)
    {
        if (string.IsNullOrWhiteSpace(abbr)) return "en";

        return abbr[..Math.Min(3, abbr.Length)].ToLowerInvariant() switch
        {
            "spa" => "es",
            "por" => "pt",
            "ger" or "deu" => "de",
            "fre" => "fr",
            "ita" => "it",
            "rus" => "ru",
            "chi" => "zh",
            "kor" => "ko",
            "jap" => "ja",
            "heb" or "wlc" or "map" => "he",
            "grk" or "grc" or "byz" or "tr " or "sta" => "el",
            "lat" or "vul" => "la",
            "nor" => "no",
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