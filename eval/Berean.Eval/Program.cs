using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// Berean.Eval — runs the question set through the pipeline (no SignalR) and writes
//   eval/runs/<date>-<label>/<id>.md   answer + what the model was given + tradition breakdown
//   eval/runs/<date>-<label>/summary.md
//
//   dotnet run --project eval/Berean.Eval -- --label baseline
//
//   --label <name>        run name (folder suffix)
//   --only id1,id2        run just these questions
//   --retrieval-only      skip the model; measure retrieval only (fast, model-independent)
//   --mode compare        run every question in Compare mode
//   --provider <name>     ollama | claudecode | anthropic | openai (default: Llm:Provider from appsettings)
//   --model <name>        model for that provider (default: Llm:Model, or Ollama:DefaultModel for ollama)
//   --db <path>           use this index file instead of the configured one (e.g. a copy)
//   --api <url>           BereanResource.Api base URL
//   --index               build any missing modules first (can take a long time)
//   --questions <path>    alternate question file

var options = EvalOptions.Parse(args);
var repoRoot = FindRepoRoot();
var evalDir = Path.Combine(repoRoot, "eval");

var questions = LoadQuestions(options.QuestionsPath ?? Path.Combine(evalDir, "questions.json"), options.Only);
var traditionMap = TraditionMap.Load(Path.Combine(evalDir, "traditions.json"));

var configDir = Path.Combine(repoRoot, "Berean.Agent.Api");

var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(configDir, "appsettings.json"), optional: false)
    .AddJsonFile(Path.Combine(configDir, "appsettings.Development.json"), optional: true)
    .AddEnvironmentVariables()
    .Build();

var ragConfig = config.GetSection("Agents:BibleAgent").Get<RetrievalOptions>()
    ?? throw new InvalidOperationException("Agents:BibleAgent is not configured.");
if (options.DbPath is not null) ragConfig.RagDbPath = options.DbPath;
if (options.ApiUrl is not null) ragConfig.ResourceApiBaseUrl = options.ApiUrl;
ragConfig.AutoIndexMissingModules = options.Index;

var ollamaEndpoint = config["Ollama:Endpoint"] ?? "http://localhost:11434";
var embeddingModel = config["Ollama:EmbeddingModel"] ?? "mxbai-embed-large";

var llmConfig = config.GetSection("Llm").Get<LlmConfig>() ?? new LlmConfig();
llmConfig.OllamaEndpoint = ollamaEndpoint;
var provider = options.Provider ?? llmConfig.Provider;
var model = options.Model
    ?? (provider == LlmProvider.Ollama ? config["Ollama:DefaultModel"] ?? "llama3.1:8b" : llmConfig.Model);

using var logFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
var evalLog = logFactory.CreateLogger("eval");

Console.WriteLine(options.RetrievalOnly
    ? $"Eval '{options.Label}': {questions.Count} question(s), retrieval only"
    : $"Eval '{options.Label}': {questions.Count} question(s), {provider}/{model}");
Console.WriteLine($"Index: {ragConfig.RagDbPath}");

var knowledge = await BibleKnowledge.CreateAsync(ragConfig, logFactory, embeddingModel, ollamaEndpoint);
if (options.Index)
{
    var count = await knowledge.StartIndexingAsync(evalLog);
    if (count > 0) Console.WriteLine($"Building {count} missing module(s) (this can take a long time)...");
    await knowledge.IndexingTask;
}
Console.WriteLine($"Index: {knowledge.IndexedChunks} chunks" +
    (knowledge.PendingModules.Count > 0 ? $" (not indexed: {string.Join(", ", knowledge.PendingModules)})" : ""));

// A retrieval-only run never calls the model, so it doesn't need credentials or a CLI.
var llm = ChatClientFactory.Create(llmConfig, options.RetrievalOnly ? LlmProvider.Ollama : provider,
    options.RetrievalOnly ? "unused" : model, logFactory);
var pipeline = StudyPipeline.CreateBible(knowledge, llm, llmConfig, logFactory, ragConfig.Language);

var runDir = Path.Combine(evalDir, "runs", $"{DateTime.Now:yyyy-MM-dd}-{options.Label}");
Directory.CreateDirectory(runDir);

var results = new List<QuestionResult>();

foreach (var q in questions)
{
    Console.Write($"  {q.Id} ... ");
    var sw = Stopwatch.StartNew();
    var result = new QuestionResult(q);
    var mode = options.Mode ?? QueryMode.Deep;

    try
    {
        pipeline.Reset();
        var toolsBefore = pipeline.ToolInvocations.Count;
        var usageBefore = UsageTracker.Today;

        if (options.RetrievalOnly)
        {
            result.Retrieval = await pipeline.Router!.RouteAsync(q.Question, new RouteOptions(q.IncludeSda, mode));
        }
        else
        {
            result.Answer = await pipeline.ChatAsync(q.Question, mode, q.IncludeSda);
            result.Retrieval = pipeline.LastRetrieval;
            result.Tools = pipeline.ToolInvocations.Skip(toolsBefore).Select(t => $"{t.ToolName}({t.Arguments})").ToList();
            var usageAfter = UsageTracker.Today;
            result.InputTokens = usageAfter.Input - usageBefore.Input;
            result.OutputTokens = usageAfter.Output - usageBefore.Output;
        }
    }
    catch (Exception ex)
    {
        result.Error = ex.Message;
    }

    result.Elapsed = sw.Elapsed;
    result.Analyse(traditionMap);
    results.Add(result);

    File.WriteAllText(Path.Combine(runDir, q.Id + ".md"), Reports.QuestionReport(result));
    Console.WriteLine(result.Error is null ? $"{sw.Elapsed.TotalSeconds:F0}s" : "ERROR " + result.Error);
}

File.WriteAllText(Path.Combine(runDir, "summary.md"), Reports.Summary(options, options.RetrievalOnly ? "" : $"{provider}/{model}", results));
Console.WriteLine($"Done. Wrote {runDir}");
return 0;

// ── helpers ────────────────────────────────────────────────────────────────

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Berean.sln")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not find the solution root from " + AppContext.BaseDirectory);
}

static List<EvalQuestion> LoadQuestions(string path, HashSet<string>? only)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var list = new List<EvalQuestion>();
    foreach (var el in doc.RootElement.GetProperty("questions").EnumerateArray())
    {
        var q = new EvalQuestion(
            el.GetProperty("id").GetString()!,
            el.GetProperty("group").GetString()!,
            el.GetProperty("question").GetString()!,
            el.TryGetProperty("includeSda", out var s) && s.GetBoolean(),
            el.TryGetProperty("rubric", out var r) ? r.GetString() ?? "" : "");
        if (only is null || only.Contains(q.Id)) list.Add(q);
    }
    return list;
}

// ── types ──────────────────────────────────────────────────────────────────

record EvalQuestion(string Id, string Group, string Question, bool IncludeSda, string Rubric);

class EvalOptions
{
    public string Label { get; private set; } = "run";
    public string? QuestionsPath { get; private set; }
    public string? Model { get; private set; }
    public LlmProvider? Provider { get; private set; }
    public string? DbPath { get; private set; }
    public string? ApiUrl { get; private set; }
    public bool RetrievalOnly { get; private set; }
    public bool Index { get; private set; }
    public QueryMode? Mode { get; private set; }
    public HashSet<string>? Only { get; private set; }

    public static EvalOptions Parse(string[] args)
    {
        var o = new EvalOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--label": o.Label = args[++i]; break;
                case "--questions": o.QuestionsPath = args[++i]; break;
                case "--model": o.Model = args[++i]; break;
                case "--provider": o.Provider = Enum.Parse<LlmProvider>(args[++i], ignoreCase: true); break;
                case "--db": o.DbPath = args[++i]; break;
                case "--api": o.ApiUrl = args[++i]; break;
                case "--retrieval-only": o.RetrievalOnly = true; break;
                case "--index": o.Index = true; break;
                case "--mode": o.Mode = Enum.Parse<QueryMode>(args[++i], ignoreCase: true); break;
                case "--only": o.Only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
        return o;
    }
}

/// <summary>The Phase 1 tradition map. After Phase 2, chunks carry their own tag and this only cross-checks it.</summary>
class TraditionMap
{
    private readonly Dictionary<string, string> _commentaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Title, string Tradition)> _books = [];

    public static TraditionMap Load(string path)
    {
        var map = new TraditionMap();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var p in doc.RootElement.GetProperty("commentaries").EnumerateObject())
            map._commentaries[p.Name] = p.Value.GetProperty("tradition").GetString()!;
        foreach (var p in doc.RootElement.GetProperty("books").EnumerateObject())
            map._books.Add((p.Name, p.Value.GetProperty("tradition").GetString()!));
        return map;
    }

    public string? Lookup(DocumentChunk c)
    {
        switch (c.SourceType)
        {
            case SourceType.Commentary:
                return _commentaries.GetValueOrDefault(c.Source);
            case SourceType.Book:
                foreach (var b in _books)
                    if (c.Source.StartsWith(b.Title + ", Chapter ", StringComparison.Ordinal))
                        return b.Tradition;
                return null;
            default:
                return null;
        }
    }
}

record ChunkView(ContextSource Source, string Tradition, string Module, string? Warning)
{
    public DocumentChunk Chunk => Source.Scored.Chunk;
}

class QuestionResult(EvalQuestion question)
{
    private static readonly Regex Citation = new(@"\[([SA])(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex AdventistHeading = new(@"adventist\s+perspective", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EvalQuestion Question { get; } = question;
    public string? Answer { get; set; }
    public RetrievalResult? Retrieval { get; set; }
    public List<string> Tools { get; set; } = [];
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    // Analysis
    public List<ChunkView> Chunks { get; private set; } = [];
    public Dictionary<string, int> ByTradition { get; private set; } = [];
    public Dictionary<string, int> ByModule { get; private set; } = [];
    public int TextChunks => Chunks.Count;
    public double AdventistShare => Share(Traditions.Adventist);
    public double LargestInterpretiveShare { get; private set; }
    public string LargestInterpretiveTradition { get; private set; } = "-";
    public int MaxPerModule => ByModule.Count == 0 ? 0 : ByModule.Values.Max();
    public List<string> Unclassified { get; private set; } = [];

    /// <summary>Citation problems in the answer (null = answer not checked / no problems).</summary>
    public List<string> CitationIssues { get; private set; } = [];

    public double Share(string t) => TextChunks == 0 ? 0 : (double)ByTradition.GetValueOrDefault(t) / TextChunks;

    public void Analyse(TraditionMap map)
    {
        foreach (var s in Retrieval?.Sources ?? [])
        {
            var c = s.Scored.Chunk;
            string? warning = null;
            var mapped = map.Lookup(c);
            if (mapped is not null && mapped != s.Tradition)
                warning = $"stored tag '{s.Tradition}' differs from traditions.json '{mapped}'";
            if (s.Tradition == Traditions.Unclassified) Unclassified.Add(s.ModuleId);

            Chunks.Add(new ChunkView(s, s.Tradition, s.ModuleId, warning));
            ByTradition[s.Tradition] = ByTradition.GetValueOrDefault(s.Tradition) + 1;
            ByModule[s.ModuleId] = ByModule.GetValueOrDefault(s.ModuleId) + 1;
        }

        // Diversity: the biggest interpretive tradition among the non-Adventist chunks.
        var nonSda = ByTradition
            .Where(kv => kv.Key != Traditions.Adventist && Traditions.IsInterpretive(kv.Key))
            .ToList();
        var total = nonSda.Sum(kv => kv.Value);
        if (total > 0)
        {
            var top = nonSda.OrderByDescending(kv => kv.Value).First();
            LargestInterpretiveShare = (double)top.Value / total;
            LargestInterpretiveTradition = top.Key;
        }

        CheckCitations();
    }

    /// <summary>
    /// Citations must point at sources that were in the prompt. With the SDA toggle on, the
    /// neutral part must cite only [S#] and the Adventist section only [A#]; with it off,
    /// no [A#] may appear at all.
    /// </summary>
    private void CheckCitations()
    {
        if (Answer is null) return;

        var known = (Retrieval?.Sources ?? []).Select(s => s.Id).ToHashSet();
        foreach (Match m in Citation.Matches(Answer))
            if (!known.Contains(m.Value.Trim('[', ']')))
                CitationIssues.Add($"cites {m.Value}, which was not in the prompt");

        var heading = AdventistHeading.Match(Answer);
        var neutral = heading.Success ? Answer[..heading.Index] : Answer;
        var adventist = heading.Success ? Answer[heading.Index..] : "";

        if (!Question.IncludeSda)
        {
            if (Citation.Matches(Answer).Any(m => m.Groups[1].Value == "A"))
                CitationIssues.Add("cites an [A#] source with the SDA toggle off");
            return;
        }

        if (Retrieval?.AdventistContext is not null && !heading.Success)
            CitationIssues.Add("no Adventist perspective section although Adventist sources were provided");
        if (Citation.Matches(neutral).Any(m => m.Groups[1].Value == "A"))
            CitationIssues.Add("neutral analysis cites an [A#] source");
        if (Citation.Matches(adventist).Any(m => m.Groups[1].Value == "S"))
            CitationIssues.Add("Adventist section cites an [S#] source");
    }
}

static class Reports
{
    private const int MaxPerModuleTarget = 2;

    public static string QuestionReport(QuestionResult r)
    {
        var q = r.Question;
        var sb = new StringBuilder();
        sb.AppendLine($"# {q.Id}");
        sb.AppendLine();
        sb.AppendLine($"- **Group:** {q.Group}");
        sb.AppendLine($"- **SDA toggle:** {(q.IncludeSda ? "on" : "off")}");
        sb.AppendLine($"- **Intent:** {r.Retrieval?.Intent.ToString() ?? "(none)"}");
        sb.AppendLine($"- **Time:** {r.Elapsed.TotalSeconds:F1}s");
        if (r.InputTokens + r.OutputTokens > 0)
            sb.AppendLine($"- **Tokens:** {r.InputTokens} in, {r.OutputTokens} out");
        sb.AppendLine();
        sb.AppendLine("## Question");
        sb.AppendLine();
        sb.AppendLine(q.Question);
        sb.AppendLine();
        sb.AppendLine("## Rubric");
        sb.AppendLine();
        sb.AppendLine(q.Rubric);
        sb.AppendLine();

        if (r.Error is not null)
        {
            sb.AppendLine("## Error");
            sb.AppendLine();
            sb.AppendLine(r.Error);
            sb.AppendLine();
        }

        sb.AppendLine("## Answer");
        sb.AppendLine();
        sb.AppendLine(r.Answer ?? "_(retrieval-only run)_");
        sb.AppendLine();

        if (r.CitationIssues.Count > 0)
        {
            sb.AppendLine("## Citation issues");
            sb.AppendLine();
            foreach (var i in r.CitationIssues.Distinct()) sb.AppendLine($"- {i}");
            sb.AppendLine();
        }

        if (r.Tools.Count > 0)
        {
            sb.AppendLine("## Tool calls");
            sb.AppendLine();
            foreach (var t in r.Tools) sb.AppendLine($"- `{t}`");
            sb.AppendLine();
        }

        sb.AppendLine("## Tradition breakdown (numbered sources in the prompt, excluding Bible text)");
        sb.AppendLine();
        if (r.TextChunks == 0) sb.AppendLine("_No sources retrieved._");
        else
        {
            sb.AppendLine("| Tradition | Sources | Share |");
            sb.AppendLine("|---|---:|---:|");
            foreach (var kv in r.ByTradition.OrderByDescending(k => k.Value))
                sb.AppendLine($"| {kv.Key} | {kv.Value} | {(double)kv.Value / r.TextChunks:P0} |");
            sb.AppendLine();
            sb.AppendLine("| Module | Sources |");
            sb.AppendLine("|---|---:|");
            foreach (var kv in r.ByModule.OrderByDescending(k => k.Value))
                sb.AppendLine($"| {kv.Key} | {kv.Value} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Sources");
        sb.AppendLine();
        foreach (var v in r.Chunks)
        {
            var text = v.Chunk.Text.Replace("\r", " ").Replace("\n", " ");
            if (text.Length > 240) text = text[..240] + "…";
            sb.AppendLine($"- **[{v.Source.Id}]** {v.Source.Kind} · {v.Tradition} · score {v.Source.Scored.Score:F2} · {Trim(v.Source.Label, 90)}" +
                          (v.Warning is null ? "" : $" ⚠ {v.Warning}"));
            sb.AppendLine($"  > {text}");
        }
        sb.AppendLine();

        if (r.Retrieval?.Text is not null)
        {
            sb.AppendLine("## What the model was given");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(r.Retrieval.Text);
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    public static string Summary(EvalOptions o, string model, List<QuestionResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Eval run: {o.Label}");
        sb.AppendLine();
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Model: {(o.RetrievalOnly ? "(retrieval only)" : model)}");
        var tokensIn = results.Sum(r => r.InputTokens);
        var tokensOut = results.Sum(r => r.OutputTokens);
        if (tokensIn + tokensOut > 0)
            sb.AppendLine($"- Tokens: {tokensIn} in, {tokensOut} out ({(double)(tokensIn + tokensOut) / results.Count:F0} per question); " +
                          $"answer time {results.Sum(r => r.Elapsed.TotalSeconds) / results.Count:F0}s per question");
        sb.AppendLine($"- Questions: {results.Count}");
        sb.AppendLine();

        sb.AppendLine("## Per question");
        sb.AppendLine();
        sb.AppendLine("| Question | Group | SDA | Intent | Sources | Adventist | Top non-SDA tradition | Max/module | SDA-off has 0% Adventist | Cap ≤ 2/module | Citations | Manual pass/fail |");
        sb.AppendLine("|---|---|:-:|---|---:|---:|---|---:|:-:|:-:|:-:|:-:|");
        foreach (var r in results)
        {
            var q = r.Question;
            var zeroSda = q.IncludeSda ? "n/a" : (r.AdventistShare == 0 ? "✅" : "❌");
            var cap = r.MaxPerModule <= MaxPerModuleTarget ? "✅" : "❌";
            var cites = r.Answer is null ? "n/a" : (r.CitationIssues.Count == 0 ? "✅" : $"❌ {r.CitationIssues.Count}");
            var top = r.LargestInterpretiveShare > 0
                ? $"{r.LargestInterpretiveTradition} {r.LargestInterpretiveShare:P0}" : "-";
            sb.AppendLine($"| [{q.Id}]({q.Id}.md) | {q.Group} | {(q.IncludeSda ? "on" : "off")} | " +
                          $"{r.Retrieval?.Intent.ToString() ?? "-"} | {r.TextChunks} | {r.AdventistShare:P0} | {top} | " +
                          $"{r.MaxPerModule} | {zeroSda} | {cap} | {cites} |  |");
        }
        sb.AppendLine();

        sb.AppendLine("## Aggregate tradition share by group");
        sb.AppendLine();
        var traditions = results.SelectMany(r => r.ByTradition.Keys).Distinct().OrderBy(t => t).ToList();
        sb.AppendLine("| Group | Sources | " + string.Join(" | ", traditions) + " |");
        sb.AppendLine("|---|---:|" + string.Join("", traditions.Select(_ => "---:|")));
        foreach (var g in results.GroupBy(r => r.Question.Group))
        {
            var total = g.Sum(r => r.TextChunks);
            var cells = traditions.Select(t =>
                total == 0 ? "-" : ((double)g.Sum(r => r.ByTradition.GetValueOrDefault(t)) / total).ToString("P0"));
            sb.AppendLine($"| {g.Key} | {total} | " + string.Join(" | ", cells) + " |");
        }
        sb.AppendLine();

        var withNoContext = results.Count(r => r.TextChunks == 0 && r.Error is null);
        var contested = results.Where(r => r.Question.Group == "contested").ToList();

        sb.AppendLine("## Headline numbers");
        sb.AppendLine();
        sb.AppendLine($"- Questions that got no retrieved sources at all: **{withNoContext} of {results.Count}**");

        var sdaOff = results.Where(r => !r.Question.IncludeSda).ToList();
        var leaks = sdaOff.Count(r => r.AdventistShare > 0);
        sb.AppendLine($"- SDA toggle off, questions with any Adventist source: **{leaks} of {sdaOff.Count}** (target: 0)");
        sb.AppendLine($"- Largest number of sources any one module contributed: **{results.Max(r => r.MaxPerModule)}** (target: ≤ {MaxPerModuleTarget})");

        var sdaOn = results.Where(r => r.Question.IncludeSda && r.Answer is not null).ToList();
        if (sdaOn.Count > 0)
            sb.AppendLine($"- SDA toggle on, answers with citation problems: **{sdaOn.Count(r => r.CitationIssues.Count > 0)} of {sdaOn.Count}** (target: 0)");

        if (contested.Count > 0)
        {
            var totalContested = contested.Sum(r => r.TextChunks);
            var sdaContested = contested.Sum(r => r.ByTradition.GetValueOrDefault(Traditions.Adventist));
            sb.AppendLine($"- Contested questions, Adventist share of all sources: **{(totalContested == 0 ? 0 : (double)sdaContested / totalContested):P0}**");

            var nonSda = contested.SelectMany(r => r.Chunks)
                .Where(c => c.Tradition != Traditions.Adventist && Traditions.IsInterpretive(c.Tradition)).ToList();
            if (nonSda.Count > 0)
            {
                var top = nonSda.GroupBy(c => c.Tradition).OrderByDescending(g => g.Count()).First();
                sb.AppendLine($"- Diversity: largest non-Adventist interpretive tradition = **{top.Key} {(double)top.Count() / nonSda.Count:P0}** " +
                              $"of {nonSda.Count} sources (recorded, not a pass/fail: it depends on which modules are in the library)");
            }
            else sb.AppendLine("- Diversity: no non-Adventist interpretive sources were retrieved.");
        }
        sb.AppendLine();

        var unclassified = results.SelectMany(r => r.Unclassified).Distinct().ToList();
        if (unclassified.Count > 0)
        {
            sb.AppendLine("## Unclassified modules (add to ModuleProfiles)");
            sb.AppendLine();
            foreach (var u in unclassified) sb.AppendLine($"- `{Trim(u, 90)}`");
            sb.AppendLine();
        }

        var errors = results.Where(r => r.Error is not null).ToList();
        if (errors.Count > 0)
        {
            sb.AppendLine("## Errors");
            sb.AppendLine();
            foreach (var r in errors) sb.AppendLine($"- {r.Question.Id}: {r.Error}");
        }
        return sb.ToString();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
