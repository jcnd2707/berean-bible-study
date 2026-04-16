using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Serves local cross-reference data from the scrollmapper cross_references_N.db shards.
///
/// Schema (cross_references table):
///   id, from_book (TEXT name), from_chapter, from_verse,
///   to_book (TEXT name), to_chapter, to_verse_start, to_verse_end, votes
///
/// The cross-reference data comes from openbible.info and contains ~340,000
/// verse links with a vote count indicating relevance strength, split across
/// cross_references_0.db through cross_references_6.db.
/// </summary>
public class CrossReferenceService(
    IOptions<BereanResourcesConfig> config,
    ILogger<CrossReferenceService> logger)
{
    private readonly string _dbFolder = config.Value.CrossReferencesDbFolder;

    private IEnumerable<string> GetShardPaths() =>
        Enumerable.Range(0, 7)
                  .Select(n => Path.Combine(_dbFolder, $"cross_references_{n}.db"))
                  .Where(File.Exists);

    /// <summary>
    /// Returns cross-references for a specific verse, merged across all shards and ordered by votes descending.
    /// </summary>
    /// <param name="bookName">Book name in any supported language or abbreviation.</param>
    /// <param name="chapter">Chapter number.</param>
    /// <param name="verse">Verse number.</param>
    /// <param name="minVotes">Minimum vote threshold — filters out weak associations. Default 1.</param>
    /// <param name="limit">Maximum results to return. Default 20.</param>
    /// <param name="language">Language code for reference string formatting.</param>
    public CrossReferenceResult? GetForVerse(
        string bookName,
        int chapter,
        int verse,
        int minVotes = 1,
        int limit = 20,
        string language = "en")
    {
        var bookNumber = BookMapper.ToNumber(bookName);
        if (bookNumber is null)
            throw new ArgumentException($"Unknown book name: '{bookName}'");

        var shards = GetShardPaths().ToList();
        if (shards.Count == 0)
        {
            logger.LogWarning("No cross-reference shards found in folder: {Folder}", _dbFolder);
            return null;
        }

        var fromBookName = BookMapper.ToScrollmapperName(bookNumber.Value);
        var fromRef = BookMapper.ToReference(bookNumber.Value, chapter, verse, language: language);
        var entries = new List<CrossReferenceEntry>();

        foreach (var shard in shards)
        {
            try
            {
                using var conn = Open(shard);
                using var cmd = conn.CreateCommand();

                cmd.CommandText = """
                    SELECT to_book, to_chapter, to_verse_start, to_verse_end, votes
                    FROM   cross_references
                    WHERE  from_book    = $book
                      AND  from_chapter = $chapter
                      AND  from_verse   = $verse
                      AND  votes        >= $minVotes
                    ORDER  BY votes DESC
                    """;
                cmd.Parameters.AddWithValue("$book", fromBookName);
                cmd.Parameters.AddWithValue("$chapter", chapter);
                cmd.Parameters.AddWithValue("$verse", verse);
                cmd.Parameters.AddWithValue("$minVotes", minVotes);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var toBookName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var toChapter = reader.GetInt32(1);
                    var toVerseStart = reader.GetInt32(2);
                    var toVerseEnd = reader.IsDBNull(3) ? toVerseStart : reader.GetInt32(3);
                    var votes = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

                    var toBookNumber = BookMapper.ToNumber(toBookName) ?? 0;
                    var toRef = BookMapper.ToReference(toBookNumber, toChapter, toVerseStart, toVerseEnd, language);

                    entries.Add(new CrossReferenceEntry(
                        FromReference: fromRef,
                        ToReference: toRef,
                        ToBook: toBookNumber,
                        ToChapter: toChapter,
                        ToVerseStart: toVerseStart,
                        ToVerseEnd: toVerseEnd,
                        Votes: votes
                    ));
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error reading shard {Shard} for {Ref}", shard, fromRef);
            }
        }

        return new CrossReferenceResult(
            Reference: fromRef,
            References: entries
                .OrderByDescending(e => e.Votes)
                .Take(limit)
                .ToList()
        );
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        conn.Open();
        return conn;
    }
}