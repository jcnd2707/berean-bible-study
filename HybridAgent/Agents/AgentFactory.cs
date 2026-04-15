using System.ComponentModel;
using HybridAgent.Core.Models;
using HybridAgent.Core.Tools;
using Microsoft.Data.Sqlite;

namespace HybridAgent.Core.Agents;

public static class AgentFactory
{
    // ── Car Diagnostics ───────────────────────────────────────────────────

    public static (AgentConfig config, ToolRegistry tools) CreateCarAgent(
        string? openAiKey = null)
    {
        var config = new AgentConfig
        {
            OllamaModel = "llama3.2:3b",
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
    /// dictionaryFiles: paths to .dctx / .lexx files for direct word lookup.
    /// </summary>
    public static (AgentConfig config, ToolRegistry tools) CreateBibleAgent(
        string? openAiKey = null,
        IEnumerable<string>? dictionaryFiles = null)
    {
        var config = new AgentConfig
        {
            OllamaModel = "llama3.2:3b",
            OpenAiApiKey = openAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            SystemPrompt = """
                You are a biblical scholar with expertise in hermeneutics, biblical
                languages (Hebrew, Greek, Aramaic), and historical theology.

                You receive pre-retrieved reference material before each response.
                Your role is to synthesize that material into a clear, accurate answer.

                Guidelines:
                - Interpret passages in their historical and literary context
                - Reference original language meaning when it illuminates understanding
                - Present multiple scholarly perspectives on disputed passages
                - Cite chapter and verse precisely
                - Respect all Christian traditions without favouring any denomination
                - If reference material was provided, ground your answer in it and cite sources
                - If no material was provided for a question, say so and answer from your training

                When the user asks about a specific word, call lookup_word first.
                When the user asks about a specific verse, the context has already been retrieved.
                Remember the full conversation context for follow-up questions.
                """,
        };

        var tools = new ToolRegistry();
        var dictFiles = dictionaryFiles?.ToList() ?? [];

        // ── Step 2: lookup_word — direct SQLite query against .dctx files ────
        tools.Register(
            [Description(
                "Looks up a word, name, or theological term in the biblical dictionaries and lexicons. " +
                "Use this when the user asks about the meaning of a Greek or Hebrew word, a theological " +
                "concept, a biblical name, or a Strong's number (e.g. G25, H430). " +
                "Returns the definition from all available dictionaries.")]
        async ([Description("Word, term, name, or Strong's number to look up (e.g. 'agape', 'pneuma', 'G25', 'hesed')")] string term) =>
            {
                if (dictFiles.Count == 0)
                    return "No dictionary files configured. Check DictionaryRootPath in appsettings.json.";

                var results = new System.Text.StringBuilder();
                var found = 0;

                foreach (var file in dictFiles)
                {
                    if (!File.Exists(file)) continue;

                    try
                    {
                        var cs = $"Data Source={file};Mode=ReadOnly;";
                        await using var conn = new SqliteConnection(cs);
                        await conn.OpenAsync();

                        var cmd = conn.CreateCommand();
                        // Search by exact topic first, then prefix, then substring
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
                        cmd.Parameters.AddWithValue("@exact", term);
                        cmd.Parameters.AddWithValue("@prefix", term + "%");
                        cmd.Parameters.AddWithValue("@contains", "%" + term + "%");

                        var dictName = Path.GetFileNameWithoutExtension(file);
                        await using var reader = await cmd.ExecuteReaderAsync();

                        while (await reader.ReadAsync())
                        {
                            var topic = reader.GetString(0);
                            var def = reader.GetString(1);
                            // Strip RTF/HTML markup (reuse the same logic as ESwordReader)
                            def = System.Text.RegularExpressions.Regex
                                .Replace(def, @"<[^>]+>|\{[^}]*\}|\\[a-z]+\d*\s?", " ")
                                .Trim();
                            if (def.Length > 1000) def = def[..1000] + "…";

                            results.AppendLine($"**{topic}** ({dictName}):");
                            results.AppendLine(def);
                            results.AppendLine();
                            found++;
                        }
                    }
                    catch { /* skip unreadable or encrypted files */ }
                }

                return found > 0
                    ? results.ToString()
                    : $"No definition found for '{term}' in the available dictionaries.";
            },
            "lookup_word"
        );

        // ── Step 3: get_cross_references ──────────────────────────────────
        tools.Register(
     [Description("Returns cross-references and related passages for a Bible verse using online resources." + 
     "Returns cross-references for a specific Bible verse. ALWAYS provide the 'verse' parameter — e.g. 'John 3:16'. Do not call this tool without a verse.")]
        ([Description("Verse reference, e.g. 'John 3:16' or 'Romans 8:28'")] string verse = "John 3:16")
         => $"Cross-references for {verse}: https://www.biblegateway.com/passage/?search={Uri.EscapeDataString(verse)}&version=NIV",
     "get_cross_references"
 );

        return (config, tools);
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