using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Berean.Core.Agent;

/// <summary>
/// One conversation: a StudyAgent on top of the shared library (<see cref="BibleKnowledge"/>).
///
/// Flow for every chat turn:
///   1. QueryRouter classifies the query (Verse / Definition / Conceptual / Mixed)
///   2. Router retrieves — exact verse text and commentary from the API, semantic search in the
///      index — in a neutral main pass, plus one pass per perspective selected for this conversation
///   3. The retrieved material is put in front of the question
///   4. The model answers, streaming; the tool loop handles lookup_word and friends
/// </summary>
public class StudyPipeline
{
    private readonly StudyAgent _diagnostic;
    private readonly BibleKnowledge _knowledge;
    private readonly ToolRegistry _registry;
    private readonly ILogger _log;

    public QueryRouter Router { get; }

    /// <summary>
    /// The perspective(s) selected for this conversation — set once when it starts (or resumes)
    /// and locked for its lifetime; see <see cref="RetrievalOptions.MaxPerspectivesPerQuestion"/>.
    /// </summary>
    public IReadOnlyList<Perspective> Perspectives { get; set; } = [];

    /// <summary>The model behind this conversation.</summary>
    public LlmClient Llm { get; }

    /// <summary>Identifies the conversation (see <see cref="StudyAgent.ConversationId"/>).</summary>
    public string ConversationId
    {
        get => _diagnostic.ConversationId;
        set => _diagnostic.ConversationId = value;
    }

    /// <summary>Called after each turn with what it added and what was retrieved for it.</summary>
    public Func<TurnRecord, Task>? TurnCompleted { get; set; }

    /// <summary>Continues a stored conversation from its messages.</summary>
    public void LoadHistory(IEnumerable<ChatMessage> messages) => _diagnostic.LoadHistory(messages);

    /// <summary>The Claude Code client behind this conversation, if that is the provider.</summary>
    public ClaudeCodeChatClient? ClaudeCode => Llm.Client.GetService<ClaudeCodeChatClient>();

    public int IndexedChunks => _knowledge.IndexedChunks;
    public Task IndexingTask => _knowledge.IndexingTask;
    public bool IsIndexing => _knowledge.IsIndexing;

    /// <summary>Every tool call made in this conversation.</summary>
    public IReadOnlyList<ToolResult> ToolInvocations => _registry.Invocations;

    /// <summary>Retrieval result of the most recent turn (used by the eval harness).</summary>
    public RetrievalResult? LastRetrieval { get; private set; }

    public StudyPipeline(
        BibleKnowledge knowledge,
        LlmClient llm,
        LlmConfig llmConfig,
        StudyAgentOptions agentConfig,
        ToolRegistry tools,
        string language,
        ILoggerFactory logFactory)
    {
        _knowledge = knowledge;
        Llm = llm;
        _registry = tools;
        _log = logFactory.CreateLogger<StudyPipeline>();

        Router = new QueryRouter(knowledge.Rag, knowledge.Config, knowledge.Api,
            string.IsNullOrWhiteSpace(language) ? knowledge.Config.Language : language,
            logFactory.CreateLogger<QueryRouter>());

        _diagnostic = new StudyAgent(llm, llmConfig, tools, agentConfig,
            logFactory.CreateLogger<StudyAgent>());
        _diagnostic.TurnCompleted = messages =>
            TurnCompleted is null ? Task.CompletedTask : TurnCompleted(new TurnRecord(messages, LastRetrieval));
    }

    /// <summary>Builds a conversation for the Bible study agent on the shared library.</summary>
    public static StudyPipeline CreateBible(
        BibleKnowledge knowledge,
        LlmClient llm,
        LlmConfig llmConfig,
        ILoggerFactory logFactory,
        string language = "en")
    {
        var (agentConfig, tools) = StudyAgentFactory.Create(knowledge);

        return new StudyPipeline(knowledge, llm, llmConfig, agentConfig, tools, language, logFactory);
    }

    // ── Chat ───────────────────────────────────────────────────────────────

    public async Task<string> ChatAsync(
        string userInput,
        QueryMode mode = QueryMode.Deep,
        CancellationToken ct = default)
    {
        var ragContext = await RetrieveAsync(userInput, mode, ct);
        return await _diagnostic.ChatAsync(userInput, ragContext, Perspectives, mode, ct);
    }

    /// <summary>
    /// The turn as an ordered stream of events: the sources the answer may cite (once retrieval is
    /// done), any tool the model calls, then the answer text as it is generated.
    /// </summary>
    public async IAsyncEnumerable<PipelineEvent> ChatEventsAsync(
        string userInput,
        QueryMode mode = QueryMode.Deep,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Tool calls happen inside the model call, so they arrive on another path than the
        // text; a channel merges them into one ordered stream.
        var channel = Channel.CreateUnbounded<PipelineEvent>();
        void OnTool(string name, string args) => channel.Writer.TryWrite(new ToolEvent(name, args));
        _diagnostic.ToolStarted += OnTool;

        var producer = Task.Run(async () =>
        {
            try
            {
                var ragContext = await RetrieveAsync(userInput, mode, ct);
                if (LastRetrieval is { } retrieval)
                    channel.Writer.TryWrite(new SourcesEvent(retrieval));

                await foreach (var text in _diagnostic.ChatStreamAsync(userInput, ragContext, Perspectives, mode, ct))
                    channel.Writer.TryWrite(new TextEvent(text));

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var ev in channel.Reader.ReadAllAsync(ct))
                yield return ev;
        }
        finally
        {
            _diagnostic.ToolStarted -= OnTool;
            try { await producer; } catch { /* already surfaced through the channel */ }
        }
    }

    private async Task<string?> RetrieveAsync(string userInput, QueryMode mode, CancellationToken ct)
    {
        LastRetrieval = null;
        if (mode == QueryMode.Quick) return null;

        var result = await Router.RouteAsync(userInput, new RouteOptions(Perspectives, mode), ct);
        LastRetrieval = result;

        _log.LogInformation("[Pipeline] Intent={Intent} perspectives={Perspectives} sources={Count}",
            result.Intent, Perspectives.Count == 0 ? "-" : string.Join(",", Perspectives.Select(p => p.Id)), result.Sources.Count);

        return result.Text;
    }

    public void Reset() => _diagnostic.Reset();
    public int MessageCount => _diagnostic.MessageCount;
}

/// <summary>One step of a chat turn, in the order it happens.</summary>
public abstract record PipelineEvent;

/// <summary>Retrieval finished: the numbered sources the answer may cite.</summary>
public sealed record SourcesEvent(RetrievalResult Retrieval) : PipelineEvent;

/// <summary>The model called a tool (name, arguments as JSON).</summary>
public sealed record ToolEvent(string Name, string Arguments) : PipelineEvent;

/// <summary>A piece of the answer text.</summary>
public sealed record TextEvent(string Text) : PipelineEvent;

/// <summary>What one turn added to the conversation, and the sources retrieved for it.</summary>
public sealed record TurnRecord(IReadOnlyList<ChatMessage> Messages, RetrievalResult? Retrieval);
