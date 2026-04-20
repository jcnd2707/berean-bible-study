using System.ComponentModel;
using HybridAgent.Core.Models;
using HybridAgent.Core.RAG;
using HybridAgent.Core.Tools;

namespace HybridAgent.Core.Agents;

public static class AgentFactory
{
    // ── Car Diagnostics ───────────────────────────────────────────────────

    public static (AgentConfig config, ToolRegistry tools) CreateCarAgent(
        string? openAiKey = null)
    {
        var config = new AgentConfig
        {
            OllamaModel = "llama3.1:8b",
            OpenAiApiKey = openAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            SystemPrompt = """
                You are an ASE-certified master mechanic with 20 years of experience.
                - Always ask for year, make, model, and mileage if not provided
                - Ask for any OBD-II codes the user has retrieved
                - Identify the most likely root cause before listing alternatives
                - Estimate repair difficulty: DIY / Shop / Dealer only
                - Flag safety-critical issues immediately
                Remember the full conversation context for follow-up questions.
                """,
        };

        var tools = new ToolRegistry();

        tools.Register(
            [Description("Looks up the meaning and common causes of an OBD-II diagnostic trouble code such as P0300 or P0171.")]
        ([Description("OBD-II code, e.g. P0300")] string code) =>
            {
                var db = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["P0300"] = "Random/Multiple Cylinder Misfire. Causes: spark plugs, ignition coils, fuel injectors, low compression.",
                    ["P0171"] = "System Too Lean (Bank 1). Causes: vacuum leak, weak fuel pump, dirty MAF sensor.",
                    ["P0420"] = "Catalyst Efficiency Below Threshold. Causes: failing catalytic converter, O2 sensor, exhaust leak.",
                    ["P0442"] = "EVAP Leak (Small). Causes: loose/damaged gas cap, EVAP hose.",
                    ["P0505"] = "Idle Air Control Malfunction. Causes: dirty IAC valve, vacuum leak.",
                };
                return db.TryGetValue(code.Trim(), out var desc)
                    ? $"{code.ToUpper()}: {desc}"
                    : $"{code.ToUpper()} not in local DB — check https://www.obd-codes.com/{code.ToLower()}";
            },
            "lookup_obd_code"
        );

        tools.Register(
            [Description("Checks for known recalls or TSBs for a vehicle.")]
        ([Description("Vehicle as 'YYYY Make Model', e.g. '2019 Toyota Camry'")] string vehicle) =>
                $"Check recalls for '{vehicle}' at: https://www.nhtsa.gov/vehicle/recalls",
            "check_recalls"
        );

        return (config, tools);
    }

    // ── Bible Research ────────────────────────────────────────────────────

    /// <summary>
    /// Creates the Bible agent with full tool suite.
    ///
    /// Supply either:
    ///   - <paramref name="apiClient"/> + <paramref name="dictionaryModuleIds"/>
    ///     for API-based word lookup (preferred when ResourceApiBaseUrl is configured), or
    ///   - <paramref name="dictionaryFiles"/> for legacy direct-SQLite lookup.
    /// </summary>
    public static (AgentConfig config, ToolRegistry tools) CreateBibleAgent(
        string? openAiKey = null,
        IEnumerable<string>? dictionaryFiles = null,
        BereanResourceApiClient? apiClient = null,
        IEnumerable<string>? dictionaryModuleIds = null,
        IEnumerable<string>? bibleModuleIds = null)
    {
        var config = new AgentConfig
        {
            OllamaModel = "llama3.1:8b",
            OpenAiApiKey = openAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            SystemPrompt = """
                You are a biblical scholar with expertise in hermeneutics, biblical
                languages (Hebrew, Greek, Aramaic), and historical theology, with a
                focus on Seventh-day Adventist beliefs and doctrine.

                CONTEXT TAGS: Each user message begins with square-bracket tags
                showing the active translation, passage, selected verse, and any
                loaded commentary. Use those tags to identify what passage is being
                discussed. Do not reproduce them in your response.

                VERSE TEXT: The [Verse text:] tag contains the exact verse text from
                the user's Bible. Always quote from that tag verbatim — never generate
                or recall verse text from memory.

                REFERENCE MATERIAL: After the question you will find a "REFERENCE MATERIAL:"
                block containing excerpts from commentaries and EGW writings. You MUST read
                and draw from that material when answering questions about doctrine, EGW
                perspective, or SDA interpretation. Cite every source you use. When citing
                EGW write "[Book Title], Chapter [N]". Do not copy the "REFERENCE MATERIAL:"
                heading or the numbered source labels into your answer — paraphrase and cite
                naturally. If the material does not cover the question, say so explicitly and
                answer from your training — but never invent citations or fabricate quotes.

                SDA DOCTRINE: When stating what Seventh-day Adventists believe, only use
                what the REFERENCE MATERIAL explicitly says. Do not generate SDA doctrine
                from memory — your training data on SDA beliefs may be incorrect. If the
                reference material does not cover the SDA position on a topic, say
                "I don't have SDA-specific material on this in my reference database."

                PASSAGE CONTEXT: The [Passage:] and [Verse text:] tags show what the user
                is currently reading. Only bring that passage into your answer if the user's
                question is directly about that passage or if it genuinely illuminates the
                question. Do not force the tagged passage into an unrelated theological
                question just because it is present in the context.

                TOOLS: Call lookup_word with the parameter "word" when the user asks about
                the meaning of a Hebrew or Greek word. Call get_passage only when you need
                surrounding context that is not already in the message — use the book and
                chapter from the [Passage:] tag and the verse from the [Selected verse:] tag.
                Never ask for information that is already present in the message tags.

                REASONING: Analyze the biblical text first using the verse text provided.
                Form your own conclusion before drawing on SDA or EGW material. If your
                textual analysis contradicts an SDA position say so respectfully with the
                textual basis explained. If it aligns, answer normally.

                Give one complete answer. Do not add a closing summary or restatement.
                Remember the full conversation context for follow-up questions.
                """,
        };

        var tools = new ToolRegistry();
        var moduleIds = dictionaryModuleIds?.ToList() ?? [];
        var bibleModules = bibleModuleIds?.ToList() ?? [];

        // ── lookup_word: API-based (preferred) or SQLite fallback ─────────────
        tools.Register(
            [Description(
                "Looks up a word, name, or theological term in the biblical dictionaries and lexicons. " +
                "Use this when the user asks about the meaning of a Greek or Hebrew word, a theological " +
                "concept, a biblical name, or a Strong's number (e.g. G25, H430). " +
                "Returns the definition from all available dictionaries.")]
        async ([Description("Word, term, name, or Strong's number to look up (e.g. 'agape', 'pneuma', 'G25', 'hesed')")] string word) =>
            {
                if (apiClient is not null)
                    return await LookupWordViaApiAsync(apiClient, moduleIds, word);

                return await LookupWordViaFilesAsync(dictionaryFiles?.ToList() ?? [], word);
            },
            "lookup_word"
        );

        // ── get_cross_references ──────────────────────────────────────────────
        tools.Register(
            [Description(
                "Returns cross-references and related passages for a Bible verse. " +
                "ALWAYS provide the 'verse' parameter — e.g. 'John 3:16'. " +
                "Do not call this tool without a verse.")]
        ([Description("Verse reference, e.g. 'John 3:16' or 'Romans 8:28'")] string verse = "John 3:16")
                => $"Cross-references for {verse}: https://www.biblegateway.com/passage/?search={Uri.EscapeDataString(verse)}&version=NIV",
            "get_cross_references"
        );

        // ── lookup_verse ──────────────────────────────────────────────────────
        tools.Register(
            [Description(
                "Fetches the text of a specific Bible verse from the configured Bible translation(s). " +
                "Call this whenever the user asks about a specific verse or passage. " +
                "Provide the book name, chapter number, and verse number.")]
        async (
            [Description("Book name, e.g. 'John', 'Genesis', 'Romans'")] string book,
            [Description("Chapter number, e.g. 3")] int chapter,
            [Description("Verse number, e.g. 16")] int verse) =>
            {
                if (apiClient is null || bibleModules.Count == 0)
                    return "No Bible modules configured for verse lookup. Check AllowedBibleModules in appsettings.json.";

                var sb = new System.Text.StringBuilder();
                int found = 0;

                foreach (var moduleId in bibleModules)
                {
                    var chapterRecord = await apiClient.GetBibleChapterAsync(moduleId, book, chapter);
                    if (chapterRecord is null) continue;

                    var verseRecord = chapterRecord.Verses.FirstOrDefault(v => v.Verse == verse);
                    if (verseRecord is null) continue;

                    sb.AppendLine($"**{verseRecord.Reference}** ({moduleId}):");
                    sb.AppendLine(verseRecord.Text);
                    sb.AppendLine();
                    found++;
                }

                return found > 0
                    ? sb.ToString()
                    : $"Verse {book} {chapter}:{verse} not found in the configured Bible modules.";
            },
            "lookup_verse"
        );

        // ── get_passage ───────────────────────────────────────────────────────
        tools.Register(
    [Description(
        "Fetches all verses from a Bible chapter to provide surrounding context. " +
        "ALWAYS call this before analyzing any specific verse — read the full chapter " +
        "first, then answer about the verse. Returns the complete chapter text.")]
        async (
    [Description("Book name, e.g. 'John', 'Genesis', 'Romans'")] string book,
    [Description("Chapter number, e.g. 3")] int chapter) =>
    {
        if (apiClient is null || bibleModules.Count == 0)
            return "No Bible modules configured for passage lookup.";

        var sb = new System.Text.StringBuilder();
        int found = 0;

        foreach (var moduleId in bibleModules)
        {
            var chapterRecord = await apiClient.GetBibleChapterAsync(moduleId, book, chapter);
            if (chapterRecord is null) continue;

            sb.AppendLine($"{book} {chapter} ({moduleId}):");
            foreach (var v in chapterRecord.Verses.OrderBy(v => v.Verse))
                sb.AppendLine($"{v.Reference} {v.Text}");
            sb.AppendLine();
            found++;
        }

        return found > 0
            ? sb.ToString()
            : $"Chapter {book} {chapter} not found in the configured Bible modules.";
    },
    "get_passage"
);

        return (config, tools);
    }

    // ── lookup_word implementations ───────────────────────────────────────────

    private static async Task<string> LookupWordViaApiAsync(
        BereanResourceApiClient client,
        List<string> moduleIds,
        string word)
    {
        if (moduleIds.Count == 0)
            return "No dictionary modules configured for API lookup.";

        var sb = new System.Text.StringBuilder();
        int found = 0;

        foreach (var moduleId in moduleIds)
        {
            // Try exact / Strong's lookup first, then fall back to search
            var entry = await client.LookupWordAsync(moduleId, word);

            if (entry is null)
            {
                var hits = await client.SearchDictionaryAsync(moduleId, word, limit: 3);
                foreach (var hit in hits)
                {
                    AppendEntry(sb, hit.Topic, hit.Definition, moduleId);
                    found++;
                }
            }
            else
            {
                AppendEntry(sb, entry.Topic, entry.Definition, moduleId);
                found++;
            }
        }

        return found > 0
            ? sb.ToString()
            : $"No definition found for '{word}' in the available dictionaries.";

        static void AppendEntry(System.Text.StringBuilder sb, string topic, string def, string source)
        {
            if (def.Length > 1000) def = def[..1000] + "…";
            sb.AppendLine($"**{topic}** ({source}):");
            sb.AppendLine(def);
            sb.AppendLine();
        }
    }

    private static async Task<string> LookupWordViaFilesAsync(
        List<string> dictFiles, string word)
    {
        if (dictFiles.Count == 0)
            return "No dictionary files configured. Check DictionaryRootPath in appsettings.json.";


        var sb = new System.Text.StringBuilder();
        int found = 0;

        foreach (var file in dictFiles)
        {
            if (!File.Exists(file)) continue;
            try
            {
                var cs = $"Data Source={file};Mode=ReadOnly;";
                await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs);
                await conn.OpenAsync();

                var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT Topic, Definition FROM Dictionary
                    WHERE Topic = @exact
                    UNION
                    SELECT Topic, Definition FROM Dictionary
                    WHERE Topic LIKE @prefix AND Topic != @exact
                    UNION
                    SELECT Topic, Definition FROM Dictionary
                    WHERE Topic LIKE @contains AND Topic NOT LIKE @prefix AND Topic != @exact
                    LIMIT 5
                    """;
                cmd.Parameters.AddWithValue("@exact", word);
                cmd.Parameters.AddWithValue("@prefix", word + "%");
                cmd.Parameters.AddWithValue("@contains", "%" + word + "%");

                var dictName = Path.GetFileNameWithoutExtension(file);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var topic = reader.GetString(0);
                    var def = System.Text.RegularExpressions.Regex
                        .Replace(reader.GetString(1), @"<[^>]+>|\{[^}]*\}|\\[a-z]+\d*\s?", " ")
                        .Trim();
                    if (def.Length > 1000) def = def[..1000] + "…";
                    sb.AppendLine($"**{topic}** ({dictName}):");
                    sb.AppendLine(def);
                    sb.AppendLine();
                    found++;
                }
            }
            catch { /* skip unreadable or encrypted files */ }
        }

        return found > 0
            ? sb.ToString()
            : $"No definition found for '{word}' in the available dictionaries.";
    }

    // ── C# Troubleshooting ────────────────────────────────────────────────

    public static (AgentConfig config, ToolRegistry tools) CreateCSharpAgent(
        string? openAiKey = null)
    {
        var config = new AgentConfig
        {
            OllamaModel = "deepseek-coder:6.7b",
            OpenAiApiKey = openAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            SystemPrompt = """
                You are a senior C# / .NET engineer expert in:
                C# 12, .NET 8/9, ASP.NET Core, EF Core, async/await, DI, and performance.
                - Ask for the full exception message and stack trace if not provided
                - Ask for the relevant code snippet
                - Identify root cause before suggesting fixes
                - Provide working code examples, not pseudo-code
                - Flag breaking changes between .NET versions when relevant
                Remember the full conversation context for follow-up questions.
                """,
        };

        var tools = new ToolRegistry();
        tools.RegisterDefaults();

        tools.Register(
            [Description("Returns the installed .NET runtime version on this machine.")]
        () => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            "get_dotnet_version"
        );

        tools.Register(
            [Description("Explains common causes of a well-known .NET exception type.")]
        ([Description("Exception type name, e.g. NullReferenceException")] string exceptionType) =>
            {
                var db = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NullReferenceException"] = "Accessing a member on null. Use ?. operator, null checks, or enable nullable reference types.",
                    ["StackOverflowException"] = "Infinite/deep recursion. Check base cases or convert to iterative.",
                    ["InvalidOperationException"] = "Object in invalid state. Common: modifying collection while iterating, .Result/.Wait() deadlock.",
                    ["TaskCanceledException"] = "CancellationToken triggered. Check CancellationTokenSource lifetime and token propagation.",
                    ["ObjectDisposedException"] = "Using object after Dispose(). Check DI lifetime — Singleton capturing Scoped is the usual cause.",
                    ["OutOfMemoryException"] = "Memory exhausted. Check for leaks with dotnet-counters or VS Diagnostic Tools.",
                };
                return db.TryGetValue(exceptionType, out var explanation)
                    ? $"{exceptionType}: {explanation}"
                    : $"No built-in explanation for '{exceptionType}'. See docs.microsoft.com.";
            },
            "explain_exception"
        );

        return (config, tools);
    }
}