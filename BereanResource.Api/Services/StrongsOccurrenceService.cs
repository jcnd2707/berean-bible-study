using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Where each Strong's number is used in a tagged Bible module ("how is G26 used across the NT").
///
/// The Bible modules only tag words verse by verse, so this scans a module once into a small
/// SQLite table (<c>Occurrences(Module, Number, Book, Chapter, Verse, Word)</c>) the first time it
/// is asked for, and answers from that table afterwards. The table lives next to the notes
/// database and survives restarts.
/// </summary>
public class StrongsOccurrenceService(
    BibleService bible,
    IOptions<BereanResourcesConfig> config,
    ILogger<StrongsOccurrenceService> logger)
{
    private readonly string _dbPath = Path.Combine(
        Path.GetDirectoryName(config.Value.NotesDbPath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory,
        "strongs-occurrences.db");

    private readonly object _buildLock = new();

    /// <summary>
    /// Count, spread by book, and a sample of verses for a Strong's number. Null when the module
    /// carries no Strong's tags. The first call for a module scans it (about a minute).
    /// </summary>
    public StrongsOccurrences? Find(string moduleId, string number, int sampleSize = 20)
    {
        var info = bible.GetTranslationInfo(moduleId);
        if (info is null || !info.HasStrongs) return null;

        EnsureBuilt(moduleId);
        number = Normalise(number);

        using var conn = Open();
        var rows = new List<(int Book, int Chapter, int Verse, string Word)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Book, Chapter, Verse, Word FROM Occurrences
                WHERE Module = $m AND Number = $n
                ORDER BY Book, Chapter, Verse
                """;
            cmd.Parameters.AddWithValue("$m", moduleId);
            cmd.Parameters.AddWithValue("$n", number);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3)));
        }

        var byBook = rows.GroupBy(x => x.Book)
            .Select(g => new StrongsBookCount(g.Key, BookMapper.ToName(g.Key, "en"), g.Count()))
            .ToList();

        // A sample that spreads across books instead of being the first N verses of Genesis.
        var sample = new List<StrongsSample>();
        var queues = rows.GroupBy(x => x.Book).Select(g => new Queue<(int Book, int Chapter, int Verse, string Word)>(g)).ToList();
        while (sample.Count < sampleSize && queues.Any(q => q.Count > 0))
            foreach (var q in queues.Where(q => q.Count > 0))
            {
                if (sample.Count >= sampleSize) break;
                var (book, chapter, verse, word) = q.Dequeue();
                var v = bible.GetVerse(moduleId, BookMapper.ToName(book, "en"), chapter, verse);
                sample.Add(new StrongsSample($"{BookMapper.ToName(book, "en")} {chapter}:{verse}", word, v?.Text ?? ""));
            }

        return new StrongsOccurrences(number, rows.Count, byBook, sample);
    }

    /// <summary>"g026" / "G26" / "g26" → "G26".</summary>
    public static string Normalise(string number)
    {
        number = number.Trim();
        if (number.Length < 2) return number.ToUpperInvariant();
        var digits = number[1..].TrimStart('0');
        return $"{char.ToUpperInvariant(number[0])}{(digits.Length == 0 ? "0" : digits)}";
    }

    // ── Building the table ────────────────────────────────────────────────────

    private void EnsureBuilt(string moduleId)
    {
        lock (_buildLock)
        {
            using var conn = Open();
            Exec(conn, """
                CREATE TABLE IF NOT EXISTS Occurrences (
                    Module TEXT NOT NULL, Number TEXT NOT NULL,
                    Book INTEGER NOT NULL, Chapter INTEGER NOT NULL, Verse INTEGER NOT NULL,
                    Word TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_Occurrences ON Occurrences (Module, Number);
                CREATE TABLE IF NOT EXISTS Built (Module TEXT PRIMARY KEY, BuiltAt TEXT NOT NULL);
                """);

            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM Built WHERE Module = $m";
                check.Parameters.AddWithValue("$m", moduleId);
                if ((long)check.ExecuteScalar()! > 0) return;
            }

            logger.LogInformation("Building the Strong's occurrence table for {Module} (first use)…", moduleId);
            using var tx = conn.BeginTransaction();

            // A half-finished earlier attempt must not double the counts.
            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM Occurrences WHERE Module = $m";
                clear.Parameters.AddWithValue("$m", moduleId);
                clear.ExecuteNonQuery();
            }

            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO Occurrences (Module, Number, Book, Chapter, Verse, Word) VALUES ($m,$n,$b,$c,$v,$w)";
            var pM = insert.Parameters.Add("$m", SqliteType.Text);
            var pN = insert.Parameters.Add("$n", SqliteType.Text);
            var pB = insert.Parameters.Add("$b", SqliteType.Integer);
            var pC = insert.Parameters.Add("$c", SqliteType.Integer);
            var pV = insert.Parameters.Add("$v", SqliteType.Integer);
            var pW = insert.Parameters.Add("$w", SqliteType.Text);
            pM.Value = moduleId;

            var total = 0;
            foreach (var book in bible.GetBooks(moduleId))
                for (var chapter = 1; chapter <= book.ChapterCount; chapter++)
                {
                    var record = bible.GetChapter(moduleId, book.Name, chapter);
                    if (record is null) continue;

                    // Some modules return each verse several times; count a verse once.
                    foreach (var verse in record.Verses.GroupBy(v => v.Verse).Select(g => g.First()))
                        foreach (var w in verse.StrongsWords ?? [])
                        {
                            pN.Value = Normalise(w.Number);
                            pB.Value = book.Number; pC.Value = chapter; pV.Value = verse.Verse;
                            pW.Value = w.Word;
                            insert.ExecuteNonQuery();
                            total++;
                        }
                }

            using (var done = conn.CreateCommand())
            {
                done.Transaction = tx;
                done.CommandText = "INSERT OR REPLACE INTO Built (Module, BuiltAt) VALUES ($m, $t)";
                done.Parameters.AddWithValue("$m", moduleId);
                done.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                done.ExecuteNonQuery();
            }
            tx.Commit();
            logger.LogInformation("Strong's occurrence table for {Module}: {Count} tagged words", moduleId, total);
        }
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
