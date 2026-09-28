using System.Text.Json;
using Berean.Agent.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Berean.Core.Tests;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly TempDb _db = new();
    private ConversationStore Store() => new(_db.Path.Replace("index.rag.db", "chat.db"), NullLogger<ConversationStore>.Instance);
    public void Dispose() => _db.Dispose();

    private static ConversationInfo Info(string id, string title = "A question", string? updated = null, string? profileId = "p1") =>
        new(id, title, "2026-01-01T00:00:00Z", updated ?? "2026-01-01T00:00:00Z", "John chapter 3", "opus", null, ProfileId: profileId);

    private static StoredMessage Msg(ChatMessage m, string? sources = null) => new(
        m.Role == ChatRole.User ? "user" : m.Role == ChatRole.Assistant ? "assistant" : "tool",
        m.Text, ConversationStore.Serialize(m), sources, "2026-01-01T00:00:00Z");

    [Fact]
    public async Task CreateGetListDelete()
    {
        var store = Store();
        await store.CreateAsync(Info("a", updated: "2026-01-01T00:00:00Z"));
        await store.CreateAsync(Info("b", "Second", updated: "2026-02-01T00:00:00Z"));

        Assert.True(await store.ExistsAsync("a"));
        Assert.Equal("John chapter 3", (await store.GetAsync("a", "p1"))!.Passage);
        Assert.Equal(["b", "a"], (await store.ListAsync("p1")).Select(c => c.Id));   // newest first

        await store.AppendAsync("a", [Msg(new ChatMessage(ChatRole.User, "hi"))], null);
        Assert.True(await store.DeleteAsync("a", "p1"));
        Assert.False(await store.ExistsAsync("a"));
        Assert.Empty(await store.GetMessagesAsync("a"));          // messages go with the conversation
        Assert.Single(await store.ListAsync("p1"));
    }

    [Fact]
    public async Task GetListDelete_AreScopedToOneProfile_AndAForeignDeleteRemovesNoMessages()
    {
        var store = Store();
        await store.CreateAsync(Info("a", profileId: "alice"));
        await store.CreateAsync(Info("b", profileId: "bob"));
        await store.AppendAsync("a", [Msg(new ChatMessage(ChatRole.User, "hi"))], null);

        Assert.Null(await store.GetAsync("a", "bob"));             // wrong profile looks just like "not found"
        Assert.NotNull(await store.GetAsync("a", "alice"));
        Assert.Equal(["a"], (await store.ListAsync("alice")).Select(c => c.Id));
        Assert.Equal(["b"], (await store.ListAsync("bob")).Select(c => c.Id));

        Assert.False(await store.DeleteAsync("a", "bob"));         // foreign delete: nothing happens
        Assert.NotEmpty(await store.GetMessagesAsync("a"));        // ...including its messages
        Assert.True(await store.ExistsAsync("a"));

        Assert.True(await store.DeleteAsync("a", "alice"));
        Assert.Empty(await store.GetMessagesAsync("a"));
    }

    [Fact]
    public async Task ListAsync_SortsPinnedFirst_ThenMostRecentlyUsed()
    {
        var store = Store();
        await store.CreateAsync(Info("a", updated: "2026-01-01T00:00:00Z"));
        await store.CreateAsync(Info("b", updated: "2026-03-01T00:00:00Z"));
        await store.CreateAsync(Info("c", updated: "2026-02-01T00:00:00Z"));

        Assert.Equal(["b", "c", "a"], (await store.ListAsync("p1")).Select(x => x.Id));   // no pins: newest first

        await store.SetPinnedAsync("a", "p1", true);
        Assert.Equal(["a", "b", "c"], (await store.ListAsync("p1")).Select(x => x.Id));   // pinned jumps to the top

        await store.SetPinnedAsync("a", "p1", false);
        Assert.Equal(["b", "c", "a"], (await store.ListAsync("p1")).Select(x => x.Id));   // unpinning drops it back
    }

    [Fact]
    public async Task ListAsync_SearchesTitleAndTheProfilesOwnQuestions()
    {
        var store = Store();
        await store.CreateAsync(Info("a", "Grace and the law"));
        await store.CreateAsync(Info("b", "Something else entirely"));
        await store.AppendAsync("b", [Msg(new ChatMessage(ChatRole.User, "what about hesed and covenant love?"))], null);

        Assert.Equal(["a"], (await store.ListAsync("p1", query: "grace")).Select(x => x.Id));       // matches the title
        Assert.Equal(["b"], (await store.ListAsync("p1", query: "hesed")).Select(x => x.Id));       // matches a question
        Assert.Equal(["b", "a"], (await store.ListAsync("p1", query: null)).Select(x => x.Id));     // no query: everything
        Assert.Empty(await store.ListAsync("p1", query: "nothing matches this"));
    }

    [Fact]
    public async Task LastLocation_RoundTrips_AndAMissingOneKeepsWhatWasThere()
    {
        var store = Store();
        await store.CreateAsync(Info("a"));

        await store.AppendAsync("a", [Msg(new ChatMessage(ChatRole.User, "q1"))], null, """{"book":"Jhn","chapter":3,"verse":16}""");
        Assert.Equal("""{"book":"Jhn","chapter":3,"verse":16}""", (await store.GetAsync("a", "p1"))!.LastLocation);

        await store.AppendAsync("a", [Msg(new ChatMessage(ChatRole.User, "q2"))], null, lastLocationJson: null);
        Assert.Equal("""{"book":"Jhn","chapter":3,"verse":16}""", (await store.GetAsync("a", "p1"))!.LastLocation);

        await store.AppendAsync("a", [Msg(new ChatMessage(ChatRole.User, "q3"))], null, """{"book":"Rom","chapter":8,"verse":28}""");
        Assert.Equal("""{"book":"Rom","chapter":8,"verse":28}""", (await store.GetAsync("a", "p1"))!.LastLocation);
    }

    [Fact]
    public async Task RenameAsync_IsScopedToOneProfile()
    {
        var store = Store();
        await store.CreateAsync(Info("a", profileId: "alice"));

        await store.RenameAsync("a", "bob", "Hijacked title");
        Assert.Equal("A question", (await store.GetAsync("a", "alice"))!.Title);   // foreign rename: no-op

        await store.RenameAsync("a", "alice", "Renamed");
        Assert.Equal("Renamed", (await store.GetAsync("a", "alice"))!.Title);
    }

    [Fact]
    public async Task Adopt_MovesOnlyUnownedConversations()
    {
        var store = Store();
        await store.CreateAsync(Info("a", profileId: null));
        await store.CreateAsync(Info("b", profileId: null));
        await store.CreateAsync(Info("c", profileId: "alice"));

        Assert.Equal(2, await store.CountUnownedAsync());

        var moved = await store.AdoptUnownedAsync("alice");

        Assert.Equal(2, moved);
        Assert.Equal(0, await store.CountUnownedAsync());
        Assert.Equal(["c", "b", "a"], (await store.ListAsync("alice")).Select(x => x.Id));
    }

    [Fact]
    public async Task AnOldSchemaDatabase_GainsTheProfileColumn_AndItsRowsAreUnowned()
    {
        var path = _db.Path.Replace("index.rag.db", "chat-old.db");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Conversations (
                    Id TEXT PRIMARY KEY, Title TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                    Passage TEXT, ModelId TEXT, ClaudeSessionId TEXT
                );
                CREATE TABLE Messages (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, ConversationId TEXT NOT NULL, Role TEXT NOT NULL,
                    Content TEXT NOT NULL, MessageJson TEXT NOT NULL, SourcesJson TEXT, CreatedAt TEXT NOT NULL
                );
                INSERT INTO Conversations (Id, Title, CreatedAt, UpdatedAt, Passage, ModelId, ClaudeSessionId)
                    VALUES ('old-1', 'Old question', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'John chapter 3', 'opus', NULL);
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new ConversationStore(path, NullLogger<ConversationStore>.Instance);

        Assert.Equal(1, await store.CountUnownedAsync());
        var moved = await store.AdoptUnownedAsync("alice");
        Assert.Equal(1, moved);
        Assert.NotNull(await store.GetAsync("old-1", "alice"));
    }

    [Fact]
    public async Task Append_KeepsOrder_BumpsTheTime_AndRemembersTheClaudeSession()
    {
        var store = Store();
        await store.CreateAsync(Info("a"));

        await store.AppendAsync("a", [Msg(new ChatMessage(ChatRole.User, "q1")), Msg(new ChatMessage(ChatRole.Assistant, "a1"))], "session-1");
        await store.AppendAsync("a", [Msg(new ChatMessage(ChatRole.User, "q2"))], null);          // null must not erase the session

        Assert.Equal(["q1", "a1", "q2"], (await store.GetMessagesAsync("a")).Select(m => m.Content));
        var info = (await store.GetAsync("a", "p1"))!;
        Assert.Equal("session-1", info.ClaudeSessionId);
        Assert.True(string.CompareOrdinal(info.UpdatedAt, "2026-01-01T00:00:00Z") > 0);
    }

    [Fact]
    public async Task ToolCallsAndResults_SurviveTheRoundTrip_ForReplayToAModel()
    {
        var store = Store();
        await store.CreateAsync(Info("a"));
        var question = new ChatMessage(ChatRole.User, "[Passage: John chapter 3]\n\nWhat does hesed mean?")
        {
            AdditionalProperties = new() { ["berean.question"] = "What does hesed mean?" },
        };
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup_word", new Dictionary<string, object?> { ["word"] = "hesed" })]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "steadfast love")]);
        var answer = new ChatMessage(ChatRole.Assistant, "It means steadfast love.");
        await store.AppendAsync("a", [Msg(question), Msg(call), Msg(result), Msg(answer)], null);

        var replay = ConversationStore.ToChatMessages(await store.GetMessagesAsync("a"));

        Assert.Equal(4, replay.Count);
        var fc = Assert.IsType<FunctionCallContent>(Assert.Single(replay[1].Contents));
        Assert.Equal("lookup_word", fc.Name);
        Assert.Equal("call-1", fc.CallId);
        var fr = Assert.IsType<FunctionResultContent>(Assert.Single(replay[2].Contents));
        Assert.Equal("call-1", fr.CallId);
        Assert.Equal("It means steadfast love.", replay[3].Text);
        Assert.Contains("[Passage: John chapter 3]", replay[0].Text);   // the model still gets the tags
    }

    [Fact]
    public void ToDisplay_ShowsQuestionsAndOneBubblePerAnswer_WithSourcesAttached()
    {
        var sources = "[{\"id\":\"S1\"}]";
        var stored = new List<StoredMessage>
        {
            new("user", "What does hesed mean?", "{}", null, "t"),
            new("assistant", "Let me look that up.", "{}", null, "t"),       // text before a tool call
            new("tool", "", "{}", null, "t"),                                // hidden
            new("assistant", "It means steadfast love [S1].", "{}", sources, "t"),
            new("user", "And nephesh?", "{}", null, "t"),
            new("assistant", "Soul, life.", "{}", null, "t"),
        };

        var display = ConversationStore.ToDisplay(stored);

        Assert.Equal(["user", "agent", "user", "agent"], display.Select(d => d.Role));
        Assert.Equal("Let me look that up.\n\nIt means steadfast love [S1].", display[1].Text);
        Assert.Equal("S1", display[1].Sources!.Value[0].GetProperty("id").GetString());
        Assert.Null(display[3].Sources);
    }

    [Theory]
    [InlineData("What is grace?", "What is grace?")]
    [InlineData("[Translation: KJV]\n[Passage: John chapter 3]\n\nWhat is grace?", "What is grace?")]
    [InlineData("", "New conversation")]
    public void Title_IsTheFirstQuestion(string message, string expected)
    {
        Assert.Equal(expected, StudySessionService.MakeTitle(message));
    }

    [Fact]
    public void Title_IsClippedAtAWord()
    {
        const string question = "What does the Bible teach about the state of the dead and the resurrection of the just?";

        var title = StudySessionService.MakeTitle(question);

        Assert.EndsWith("…", title);
        var clipped = title[..^1];
        Assert.InRange(clipped.Length, 30, 60);
        Assert.StartsWith(clipped, question);
        Assert.Equal(' ', question[clipped.Length]);   // the cut fell between two words
    }

    [Fact]
    public void StoredSources_UseTheCamelCaseNamesTheClientReads()
    {
        var chunk = new DocumentChunk
        {
            Id = "x", Source = "barnes", ChunkIndex = 0, Text = "t", SourceType = SourceType.Commentary,
            ModuleId = "barnes", Tradition = Traditions.Evangelical, BookNumber = 43, ChapterBegin = 3, VerseBegin = 16,
        };
        var source = new ContextSource("S1", "commentary", "barnes", "Barnes' Notes", Traditions.Evangelical, "19th c.",
            "label", 43, 3, 16, null, new ScoredChunk(chunk, 1f));

        using var doc = JsonDocument.Parse(StudySessionService.SerializeSources([source]));
        var first = doc.RootElement[0];

        Assert.Equal("S1", first.GetProperty("id").GetString());
        Assert.Equal("commentary", first.GetProperty("kind").GetString());
        Assert.Equal("John", first.GetProperty("book").GetString());
        Assert.Equal(3, first.GetProperty("chapter").GetInt32());
    }

    [Fact]
    public void Passage_IsReadFromTheContextTag()
    {
        Assert.Equal("John chapter 3", StudySessionService.PassageOf("[Translation: KJV]\n[Passage: John chapter 3]\n\nWhy?"));
        Assert.Null(StudySessionService.PassageOf("Why?"));
    }
}
