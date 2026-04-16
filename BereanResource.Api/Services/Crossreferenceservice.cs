using BereanResourceApi.Mapping;
using BereanResourceApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BereanResourceApi.Services;

/// <summary>
/// Serves local cross-reference data from the scrollmapper cross_references.db.
///
/// Schema (cross_references table):
///   id, from_book (TEXT name), from_chapter, from_verse,
///   to_book (TEXT name), to_chapter, to_verse_start, to_verse_end, votes
///
/// The cross-reference data comes from openbible.info and contains ~340,000
/// verse links with a vote count indicating relevance strength.
/// This replaces the Agent's BibleGateway tool call with a fully local lookup.
/// </summary>
public class CrossReferenceService(
    IOptions<BereanResourcesConfig> config,
    ILogger<CrossReferenceService> logger)
{
    private readonly string _dbPath = config.Value.CrossReferencesDbPath;

    /// <summary>
    /// Returns cross-references for a specific verse, ordered by votes descending.
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

        if (!File.Exists(_dbPath))
        {
            logger.LogWarning("Cross-references database not found at {Path}", _dbPath);
            return null;
        }

        // The cross_references table uses the English book name as stored in the DB
        var fromBookName = BookMapper.ToScrollmapperName(bookNumber.Value);
        var fromRef = BookMapper.ToReference(bookNumber.Value, chapter, verse, language: language);

        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = """
                SELECT to_book, to_chapter, to_verse_start, to_verse_end, votes
                FROM   cross_references
                WHERE  from_book    = $book
                  AND  from_chapter = $chapter
                  AND  from_verse   = $verse
                  AND  votes        >= $minVotes
                ORDER  BY votes DESC
                LIMIT  $limit
                """;
            cmd.Parameters.AddWithValue("$book", fromBookName);
            cmd.Parameters.AddWithValue("$chapter", chapter);
            cmd.Parameters.AddWithValue("$verse", verse);
            cmd.Parameters.AddWithValue("$minVotes", minVotes);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();

            var entries = new List<CrossReferenceEntry>();
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

            return new CrossReferenceResult(
                Reference: fromRef,
                References: entries
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching cross-references for {Ref}", fromRef);
            return null;
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        return conn;
    }
}