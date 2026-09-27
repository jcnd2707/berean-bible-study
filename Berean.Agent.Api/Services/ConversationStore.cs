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
    string? ClaudeSessionId);

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
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Conversations (
                Id              TEXT PRIMARY KEY,
                Title           TEXT NOT NULL,
                CreatedAt       TEXT NOT NULL,
                UpdatedAt       TEXT NOT NULL,
                Passage         TEXT,
                ModelId         TEXT,
                ClaudeSessionId TEXT
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
        _log.LogInformation("[Conversations] Stored in {Path}", _dbPath);
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
            INSERT INTO Conversations (Id, Title, CreatedAt, UpdatedAt, Passage, ModelId, ClaudeSessionId)
            VALUES ($id, $title, $created, $updated, $passage, $model, $session)
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$title", c.Title);
        cmd.Parameters.AddWithValue("$created", c.CreatedAt);
        cmd.Parameters.AddWithValue("$updated", c.UpdatedAt);
        cmd.Parameters.AddWithValue("$passage", (object?)c.Passage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)c.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$session", (object?)c.ClaudeSessionId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ConversationInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, CreatedAt, UpdatedAt, Passage, ModelId, ClaudeSessionId FROM Conversations WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    /// <summary>Most recently used first.</summary>
    public async Task<List<ConversationInfo>> ListAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Title, CreatedAt, UpdatedAt, Passage, ModelId, ClaudeSessionId
            FROM Conversations ORDER BY UpdatedAt DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<ConversationInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // Foreign keys are off by default in SQLite, so delete the messages explicitly.
        cmd.CommandText = """
            DELETE FROM Messages WHERE ConversationId = $id;
            DELETE FROM Conversations WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task RenameAsync(string id, string title, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Conversations SET Title = $title WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Messages ───────────────────────────────────────────────────────────

    /// <summary>Adds a turn's messages, bumps the conversation's time and remembers its Claude Code session.</summary>
    public async Task AppendAsync(
        string conversationId, IEnumerable<StoredMessage> messages, string? claudeSessionId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var m in messages)
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
            touch.CommandText = "UPDATE Conversations SET UpdatedAt = $now, ClaudeSessionId = COALESCE($session, ClaudeSessionId) WHERE Id = $id";
            touch.Parameters.AddWithValue("$id", conversationId);
            touch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            touch.Parameters.AddWithValue("$session", (object?)claudeSessionId ?? DBNull.Value);
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
        r.IsDBNull(6) ? null : r.GetString(6));
}
