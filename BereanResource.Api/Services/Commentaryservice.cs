using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

public class CommentaryService(
    ResourceDiscoveryService discovery,
    IOptions<BereanResourcesConfig> config,
    ILogger<CommentaryService> logger)
{
    private readonly BereanResourcesConfig _cfg = config.Value;

    /// <summary>Returns all commentary entries for a chapter.</summary>
    public CommentaryChapter? GetChapter(string moduleId, string bookName, int chapter, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var path = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            SELECT Book, Chapter, VerseBegin, VerseEnd, Marker, Description
            FROM   Commentary
            WHERE  Book    = $book
              AND  Chapter = $chapter
            ORDER  BY VerseBegin
            """;
        cmd.Parameters.AddWithValue("$book", bookNumber);
        cmd.Parameters.AddWithValue("$chapter", chapter);

        using var reader = cmd.ExecuteReader();

        var entries = new List<CommentaryEntry>();
        while (reader.Read())
        {
            var b = reader.GetInt32(0);
            var ch = reader.GetInt32(1);
            var vBegin = reader.GetInt32(2);
            var vEnd = reader.GetInt32(3);
            var marker = reader.IsDBNull(4) ? null : reader.GetString(4);
            var text = reader.IsDBNull(5) ? string.Empty : StripHtml(reader.GetString(5));

            entries.Add(new CommentaryEntry(
                Book: b,
                BookName: BookMapper.ToName(b, language),
                Chapter: ch,
                VerseBegin: vBegin,
                VerseEnd: vEnd,
                Reference: BookMapper.ToReference(b, ch, vBegin, vEnd, language),
                Marker: marker,
                Text: text
            ));
        }

        if (entries.Count == 0) return null;

        return new CommentaryChapter(
            ModuleId: moduleId,
            Book: bookNumber,
            BookName: BookMapper.ToName(bookNumber, language),
            Chapter: chapter,
            Entries: entries
        );
    }

    /// <summary>Returns commentary entries that cover a specific verse.</summary>
    public List<CommentaryEntry> GetVerse(string moduleId, string bookName, int chapter, int verse, string language = "en")
    {
        var bookNumber = ResolveBookOrThrow(bookName);
        var path = ResolveOrThrow(moduleId);

        using var conn = Open(path);
        using var cmd = conn.CreateCommand();

        // A commentary entry covers a verse if VerseBegin <= verse <= VerseEnd
        cmd.CommandText = """
            SELECT Book, Chapter, VerseBegin, VerseEnd, Marker, Description
            FROM   Commentary
            WHERE  Book       = $book
              AND  Chapter    = $chapter
              AND  VerseBegin <= $verse
              AND  VerseEnd   >= $verse
            ORDER  BY VerseBegin
            """;
        cmd.Parameters.AddWithValue("$book", bookNumber);
        cmd.Parameters.AddWithValue("$chapter", chapter);
        cmd.Parameters.AddWithValue("$verse", verse);

        using var reader = cmd.ExecuteReader();

        var entries = new List<CommentaryEntry>();
        while (reader.Read())
        {
            var b = reader.GetInt32(0);
            var ch = reader.GetInt32(1);
            var vBegin = reader.GetInt32(2);
            var vEnd = reader.GetInt32(3);
            var marker = reader.IsDBNull(4) ? null : reader.GetString(4);
            var text = reader.IsDBNull(5) ? string.Empty : StripHtml(reader.GetString(5));

            entries.Add(new CommentaryEntry(
                Book: b,
                BookName: BookMapper.ToName(b, language),
                Chapter: ch,
                VerseBegin: vBegin,
                VerseEnd: vEnd,
                Reference: BookMapper.ToReference(b, ch, vBegin, vEnd, language),
                Marker: marker,
                Text: text
            ));
        }

        return entries;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string ResolveOrThrow(string moduleId)
    {
        var path = discovery.ResolvePath(_cfg.SubFolders.Commentaries, moduleId, ".cmtx");
        if (path is null) throw new FileNotFoundException($"Commentary module '{moduleId}' not found.");
        return path;
    }

    private static int ResolveBookOrThrow(string bookName)
    {
        var number = BookMapper.ToNumber(bookName);
        if (number is null) throw new ArgumentException($"Unknown book name: '{bookName}'");
        return number.Value;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }

    /// <summary>Commentary text often contains HTML — strip tags for plain text delivery.</summary>
    private static string StripHtml(string text)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
        return result.Trim();
    }
}