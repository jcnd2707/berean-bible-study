using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Berean.Agent.Api.Services;

public record ConversationInfo(
    string Id,
    string Title,
    string CreatedAt,
    string UpdatedAt,
    string? Passage,
    string? ModelId,
    string? ClaudeSessionId,
    IReadOnlyList<string>? PerspectiveIds = null,
    string? ProfileId = null, // null = unowned, written before profiles existed (see AdoptUnownedAsync)
    string? LastLocation = null, // JSON {moduleId,book,chapter,verse} — where the study ended, not just began
    bool Pinned = false,
    int QuestionCount = 0,
    int? ContextTokens = null, // the highest per-turn input-token figure reached anywhere in the session
    string? ContinuedFromId = null, // the previous part of this study, if this session continues one that filled up
    string? Recap = null, // written on the *old* session when it's continued into a new one
    string? ContinuedInId = null); // not a column — the id of the session that continues this one, queried on read

/// <summary>One stored message: the text for display, and the full message for replaying it to a model.</summary>
public record StoredMessage(
    string Role,
    string Content,
    string MessageJson,
    string? SourcesJson,
    string CreatedAt);

/// <summary>A message as the chat window shows it (tool calls and results are folded away).</summary>
public record DisplayMessage(string Role, string Text, JsonElement? Sources);

/// <summary>
/// Saved conversations in <c>chat.db</c>, so a conversation survives a refresh, a reconnect and a
/// restart. Every message is stored — tool calls and results too — so a conversation resumes with
/// its full context. Nothing here talks to a model.
/// </summary>
public sealed class ConversationStore
{
    private readonly string _dbPath;
    private readonly ILogger<ConversationStore> _log;

    public ConversationStore(string dbPath, ILogger<ConversationStore> log)
    {
        _dbPath = dbPath;
        _log = log;
        EnsureCreated();
    }

    // ── Schema ─────────────────────────────────────────────────────────────

    private void EnsureCreated()
    {
        BackUpBeforeProfilesIfNeeded();

        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS Conversations (
                    Id              TEXT PRIMARY KEY,
                    Title           TEXT NOT NULL,
                    CreatedAt       TEXT NOT NULL,
                    UpdatedAt       TEXT NOT NULL,
                    Passage         TEXT,
                    ModelId         TEXT,
                    ClaudeSessionId TEXT,
                    Perspectives    TEXT
                );
                CREATE TABLE IF NOT EXISTS Messages (
                    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE,
                    Role           TEXT NOT NULL,
                    Content        TEXT NOT NULL,
                    MessageJson    TEXT NOT NULL,
                    SourcesJson    TEXT,
                    CreatedAt      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Messages_Conversation ON Messages (ConversationId, Id);
                """;
            cmd.ExecuteNonQuery();
        }

        // Upgrades a database created before perspectives (and, later, profiles/sessions) existed.
        AddColumnIfMissing(conn, "Conversations", "Perspectives", "TEXT");
        AddColumnIfMissing(conn, "Conversations", "ProfileId", "TEXT"); // null = unowned
        AddColumnIfMissing(conn, "Conversations", "LastLocation", "TEXT");
        AddColumnIfMissing(conn, "Conversations", "Pinned", "INTEGER NOT NULL DEFAULT 0");

        var hadQuestionCount = ColumnExists(conn, "Conversations", "QuestionCount");
        AddColumnIfMissing(conn, "Conversations", "QuestionCount", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Conversations", "ContextTokens", "INTEGER");
        AddColumnIfMissing(conn, "Conversations", "ContinuedFromId", "TEXT");
        AddColumnIfMissing(conn, "Conversations", "Recap", "TEXT");
        if (!hadQuestionCount) BackfillQuestionCounts(conn);

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = """
                DROP INDEX IF EXISTS IX_Conversations_Profile;
                CREATE INDEX IX_Conversations_Profile ON Conversations (ProfileId, Pinned, UpdatedAt);
                """;
            idx.ExecuteNonQuery();
        }

        _log.LogInformation("[Conversations] Stored in {Path}", _dbPath);
    }

    /// <summary>
    /// Backs up chat.db once, the first time it's opened without a ProfileId column — mirrors
    /// NotesService's pre-profiles backup, even though this migration is additive (ALTER TABLE ADD
    /// COLUMN) rather than a full rebuild.
    /// </summary>
    private void BackUpBeforeProfilesIfNeeded()
    {
        if (!File.Exists(_dbPath)) return;

        bool needsBackup;
        using (var conn = Open())
            needsBackup = TableExists(conn, "Conversations") && !ColumnExists(conn, "Conversations", "ProfileId");
        if (!needsBackup) return;

        var backupPath = _dbPath + ".pre-profiles.bak";
        if (File.Exists(backupPath)) return;

        File.Copy(_dbPath, backupPath);
        _log.LogInformation("[Conversations] Backed up pre-profiles chat.db to {Path}", backupPath);
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        if (ColumnExists(conn, table, column)) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    /// <summary>One-time backfill (Phase 5) for conversations saved before QuestionCount existed.</summary>
    private static void BackfillQuestionCounts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Conversations SET QuestionCount = (
                SELECT COUNT(*) FROM Messages WHERE Messages.ConversationId = Conversations.Id AND Messages.Role = 'user'
            )
            """;
        cmd.ExecuteNonQuery();
    }

    // ── Conversations ──────────────────────────────────────────────────────

    public async Task<bool> ExistsAsync(string id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Conversations WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task CreateAsync(ConversationInfo c, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Conversations (Id, Title, CreatedAt, UpdatedAt, Passage, ModelId, ClaudeSessionId, Perspectives, ProfileId, ContinuedFromId)
            VALUES ($id, $title, $created, $updated, $passage, $model, $session, $perspectives, $profile, $continuedFrom)
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$title", c.Title);
        cmd.Parameters.AddWithValue("$created", c.CreatedAt);
        cmd.Parameters.AddWithValue("$updated", c.UpdatedAt);
        cmd.Parameters.AddWithValue("$passage", (object?)c.Passage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)c.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$session", (object?)c.ClaudeSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$perspectives", SerializePerspectives(c.PerspectiveIds));
        cmd.Parameters.AddWithValue("$profile", (object?)c.ProfileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$continuedFrom", (object?)c.ContinuedFromId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string SelectColumns =
        "Id, Title, CreatedAt, UpdatedAt, Passage, ModelId, ClaudeSessionId, Perspectives, ProfileId, LastLocation, Pinned, " +
        "QuestionCount, ContextTokens, ContinuedFromId, Recap";

    /// <summary>
    /// A stale or foreign id (wrong profile, or gone) comes back null just like a missing one —
    /// callers (ResumeConversationAsync) already treat that as "start fresh" rather than an error.
    /// </summary>
    public async Task<ConversationInfo?> GetAsync(string id, string profileId, CancellationToken ct = default)
    {
        await using var conn = Open();

        ConversationInfo? info;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {SelectColumns} FROM Conversations WHERE Id = $id AND ProfileId = $profile";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$profile", profileId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            info = await r.ReadAsync(ct) ? Map(r) : null;
        }
        if (info is null) return null;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM Conversations WHERE ContinuedFromId = $id LIMIT 1";
            cmd.Parameters.AddWithValue("$id", id);
            var continuedInId = await cmd.ExecuteScalarAsync(ct) as string;
            return info with { ContinuedInId = continuedInId };
        }
    }

    /// <summary>
    /// Pinned first, then most recently used, scoped to one profile. <paramref name="query"/>
    /// (case-insensitive, ASCII) matches the title or any of the profile's own questions in it —
    /// plenty for two people's data; move to FTS5 only if it ever feels slow.
    /// </summary>
    public async Task<List<ConversationInfo>> ListAsync(string profileId, string? query = null, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        var qColumns = string.Join(", ", SelectColumns.Split(", ").Select(c => $"c.{c}"));
        cmd.CommandText = $"""
            SELECT DISTINCT {qColumns}
            FROM Conversations c
            LEFT JOIN Messages m ON m.ConversationId = c.Id AND m.Role = 'user'
            WHERE c.ProfileId = $profile
              AND ($query IS NULL OR c.Title LIKE $like OR m.Content LIKE $like)
            ORDER BY c.Pinned DESC, c.UpdatedAt DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$profile", profileId);
        cmd.Parameters.AddWithValue("$query", (object?)query ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$like", string.IsNullOrWhiteSpace(query) ? DBNull.Value : $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<ConversationInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task SetPinnedAsync(string id, string profileId, bool pinned, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Conversations SET Pinned = $pinned WHERE Id = $id AND ProfileId = $profile";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profile", profileId);
        cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string id, string profileId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // Foreign keys are off by default in SQLite, so delete the messages explicitly. Both
        // statements are guarded by ProfileId, so a foreign id deletes nothing at all.
        cmd.CommandText = """
            DELETE FROM Messages WHERE ConversationId IN (SELECT Id FROM Conversations WHERE Id = $id AND ProfileId = $profile);
            DELETE FROM Conversations WHERE Id = $id AND ProfileId = $profile;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profile", profileId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> CountUnownedAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Conversations WHERE ProfileId IS NULL";
        return Convert.ToInt32((long)(await cmd.ExecuteScalarAsync(ct))!);
    }

    /// <summary>
    /// Moves every unowned conversation to <paramref name="profileId"/>. Unlike notes, conversation
    /// ids are globally unique (server-generated GUIDs), so there's no primary-key clash to guard
    /// against — every unowned conversation moves.
    /// </summary>
    public async Task<int> AdoptUnownedAsync(string profileId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Conversations SET ProfileId = $profile WHERE ProfileId IS NULL";
        cmd.Parameters.AddWithValue("$profile", profileId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RenameAsync(string id, string profileId, string title, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Conversations SET Title = $title WHERE Id = $id AND ProfileId = $profile";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profile", profileId);
        cmd.Parameters.AddWithValue("$title", title);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Written on the *old* session when "Continue in a new session" writes its recap (Phase 5).</summary>
    public async Task SetRecapAsync(string id, string profileId, string recap, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Conversations SET Recap = $recap WHERE Id = $id AND ProfileId = $profile";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$profile", profileId);
        cmd.Parameters.AddWithValue("$recap", recap);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Messages ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a turn's messages, bumps the conversation's time, remembers its Claude Code session,
    /// updates its last known reading location (a turn with none keeps whatever was there), counts
    /// the questions asked so far, and tracks the highest per-turn context size reached (Phase 5) —
    /// monotonic, so a session that once held <paramref name="contextTokens"/> tokens stays "full"
    /// even if a later turn's request happens to be smaller.
    /// </summary>
    public async Task AppendAsync(
        string conversationId, IEnumerable<StoredMessage> messages, string? claudeSessionId,
        string? lastLocationJson = null, int? contextTokens = null, CancellationToken ct = default)
    {
        var messageList = messages as IReadOnlyCollection<StoredMessage> ?? messages.ToList();
        var newQuestions = messageList.Count(m => m.Role == "user");

        await using var conn = Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var m in messageList)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Messages (ConversationId, Role, Content, MessageJson, SourcesJson, CreatedAt)
                VALUES ($c, $role, $content, $json, $sources, $at)
                """;
            cmd.Parameters.AddWithValue("$c", conversationId);
            cmd.Parameters.AddWithValue("$role", m.Role);
            cmd.Parameters.AddWithValue("$content", m.Content);
            cmd.Parameters.AddWithValue("$json", m.MessageJson);
            cmd.Parameters.AddWithValue("$sources", (object?)m.SourcesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", m.CreatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var touch = conn.CreateCommand())
        {
            touch.Transaction = tx;
            touch.CommandText = """
                UPDATE Conversations SET
                    UpdatedAt = $now,
                    ClaudeSessionId = COALESCE($session, ClaudeSessionId),
                    LastLocation = COALESCE($location, LastLocation),
                    QuestionCount = QuestionCount + $newQuestions,
                    ContextTokens = MAX(COALESCE(ContextTokens, 0), COALESCE($context, 0))
                WHERE Id = $id
                """;
            touch.Parameters.AddWithValue("$id", conversationId);
            touch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            touch.Parameters.AddWithValue("$session", (object?)claudeSessionId ?? DBNull.Value);
            touch.Parameters.AddWithValue("$location", (object?)lastLocationJson ?? DBNull.Value);
            touch.Parameters.AddWithValue("$newQuestions", newQuestions);
            touch.Parameters.AddWithValue("$context", (object?)contextTokens ?? DBNull.Value);
            await touch.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<List<StoredMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Role, Content, MessageJson, SourcesJson, CreatedAt
            FROM Messages WHERE ConversationId = $c ORDER BY Id
            """;
        cmd.Parameters.AddWithValue("$c", conversationId);
        var list = new List<StoredMessage>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new StoredMessage(r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        return list;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>The messages to hand back to a model: the complete originals, tool calls and all.</summary>
    public static List<ChatMessage> ToChatMessages(IEnumerable<StoredMessage> stored) =>
        stored.Select(m => JsonSerializer.Deserialize<ChatMessage>(m.MessageJson, AIJsonUtilities.DefaultOptions)!).ToList();

    public static string Serialize(ChatMessage message) =>
        JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions);

    /// <summary>
    /// What the chat window shows: each question, and each answer as one bubble (the text pieces
    /// around tool calls joined), with the sources of the turn attached to the answer.
    /// </summary>
    public static List<DisplayMessage> ToDisplay(IEnumerable<StoredMessage> stored)
    {
        var result = new List<DisplayMessage>();
        var answer = new System.Text.StringBuilder();
        JsonElement? sources = null;

        void FlushAnswer()
        {
            if (answer.Length > 0) result.Add(new DisplayMessage("agent", answer.ToString().Trim(), sources));
            answer.Clear();
            sources = null;
        }

        foreach (var m in stored)
        {
            if (m.Role == "user")
            {
                FlushAnswer();
                result.Add(new DisplayMessage("user", m.Content, null));
            }
            else if (m.Role == "assistant" && m.Content.Length > 0)
            {
                if (answer.Length > 0) answer.Append("\n\n");
                answer.Append(m.Content);
                if (m.SourcesJson is not null)
                    sources = JsonDocument.Parse(m.SourcesJson).RootElement.Clone();
            }
        }
        FlushAnswer();
        return result;
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static ConversationInfo Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : DeserializePerspectives(r.GetString(7)),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9),
        !r.IsDBNull(10) && r.GetInt64(10) != 0,
        QuestionCount: r.IsDBNull(11) ? 0 : (int)r.GetInt64(11),
        ContextTokens: r.IsDBNull(12) ? null : (int)r.GetInt64(12),
        ContinuedFromId: r.IsDBNull(13) ? null : r.GetString(13),
        Recap: r.IsDBNull(14) ? null : r.GetString(14));

    private static object SerializePerspectives(IReadOnlyList<string>? ids) =>
        ids is { Count: > 0 } ? JsonSerializer.Serialize(ids) : DBNull.Value;

    private static List<string> DeserializePerspectives(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
