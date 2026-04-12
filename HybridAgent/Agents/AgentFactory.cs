using HybridAgent.Core.Models;
using HybridAgent.Core.Tools;
using System.ComponentModel;

namespace HybridAgent.Core.Agents;

// ──────────────────────────────────────────────────────────────────────────────
// Each domain agent is just an AgentConfig + ToolRegistry + system prompt.
// The RAG pipeline is wired in by HybridPipeline when BuildContextAsync is called.
// ──────────────────────────────────────────────────────────────────────────────

public static class AgentFactory
{
    // ── Car Diagnostics ───────────────────────────────────────────────────

    public static (AgentConfig config, ToolRegistry tools) CreateCarAgent(
        string ollamaEndpoint = "http://localhost:11434",
        string? openAiKey = null)
    {
        var config = new AgentConfig
        {
            OllamaEndpoint = ollamaEndpoint,
            OllamaModel = "llama3.2:3b",    // fast, good for structured Q&A
            CloudModel = "gpt-4o",
            OpenAiApiKey = openAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            MaxToolRounds = 8,
            SystemPrompt = """
                You are an ASE-certified master mechanic with 20 years of experience diagnosing
                and repairing vehicles of all makes and models.

                Your approach:
                - Always ask for the vehicle year, make, model, and mileage if not provided
                - Ask for any diagnostic trouble codes (DTCs) the user has retrieved
                - Identify the most likely root cause before listing alternatives
                - Estimate repair difficulty: DIY / Shop visit / Dealer only
                - Flag any safety-critical issues immediately

                When using reference material, cite it. When uncertain, say so.
                Never guess at a diagnosis — gather facts first.
                """,
        };

        var tools = new ToolRegistry();

        // OBD-II code lookup (stub — replace with a real database or API)
        tools.Register(
            [Description("Looks up the meaning and common causes of an OBD-II diagnostic trouble code (DTC), e.g. P0300, P0171.")]
        ([Description("OBD-II code such as P0300")] string code) =>
            {
            var db = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["P0300"] = "Random/Multiple Cylinder Misfire Detected. Common causes: spark plugs, ignition coils, fuel injectors, low compression.",
                ["P0171"] = "System Too Lean (Bank 1). Common causes: vacuum leak, weak fuel pump, dirty MAF sensor, clogged fuel injector.",
                ["P0420"] = "Catalyst System Efficiency Below Threshold. Common causes: failing catalytic converter, O2 sensor, exhaust leak.",
                ["P0442"] = "Evaporative Emission Control System Leak (Small). Common causes: loose/damaged gas cap, EVAP hose leak.",
                ["P0505"] = "Idle Air Control System Malfunction. Common causes: dirty/faulty IAC valve, vacuum leak.",
                ["B0001"] = "Driver Frontal Stage 1 Deployment Control (Airbag). Requires dealer scan tool — do not ignore."
            };

        return db.TryGetValue(code.Trim(), out var desc)
            ? $"Code {code.ToUpper()}: {desc}"
            : $"Code {code.ToUpper()} not found in local database. Check https://www.obd-codes.com/{code.ToLower()} for details.";
    },
            "lookup_obd_code"
        );

        // Recall lookup stub
        tools.Register(
            [Description("Checks for known recalls or technical service bulletins (TSBs) for a vehicle.")]
            ([Description("Vehicle described as 'YYYY Make Model', e.g. '2019 Toyota Camry'")] string vehicle) =>
                $"For official recall data on '{vehicle}', check: https://www.nhtsa.gov/vehicle/recalls — " +
                $"enter your VIN for exact results. TSBs are available via ALLDATA or Mitchell1.",
            "check_recalls"
        );

        return (config, tools);
    }

// ── Bible Research ────────────────────────────────────────────────────

public static (AgentConfig config, ToolRegistry tools) CreateBibleAgent(
    string ollamaEndpoint = "http://localhost:11434",
    string? openAiKey = null)
{
    var config = new AgentConfig
    {
        OllamaEndpoint = ollamaEndpoint,
        OllamaModel = "llama3.2:3b",      // larger model for nuanced reasoning
        CloudModel = "gpt-4o",
        OpenAiApiKey = openAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        MaxToolRounds = 6,
        SystemPrompt = """
                You are a biblical scholar with expertise in hermeneutics, biblical languages
                (Hebrew, Greek, Aramaic), and historical theology.

                Your approach:
                - Interpret passages in their historical and literary context
                - Note differences across major translations (KJV, ESV, NIV, NASB) when relevant
                - Reference the original language meaning when it illuminates understanding
                - Present multiple scholarly perspectives on disputed passages
                - Distinguish between descriptive narrative and prescriptive instruction
                - Cite chapter and verse precisely

                You respect all Christian traditions and do not favour one denomination.
                When using reference material from the knowledge base, cite the source.
                """,
    };

    var tools = new ToolRegistry();

    // Cross-reference tool
    tools.Register(
        [Description("Returns cross-references and related passages for a given Bible verse.")]
    ([Description("Verse reference, e.g. 'John 3:16' or 'Romans 8:28'")] string verse) =>
            $"Cross-references for {verse} can be found at: " +
            $"https://www.biblegateway.com/passage/?search={Uri.EscapeDataString(verse)} — " +
            $"use the 'Cross References' tab. Also check Treasury of Scripture Knowledge.",
        "get_cross_references"
    );

    // Original language lookup stub
    tools.Register(
        [Description("Looks up the original Hebrew or Greek word behind a translated English word in a verse.")]
    ([Description("Book, chapter and verse, e.g. 'John 1:1'")] string verse,
         [Description("The English word to look up")] string word) =>
            $"For the original language behind '{word}' in {verse}, check Strong's Concordance: " +
            $"https://www.blueletterbible.org/search/search.cfm?Criteria={Uri.EscapeDataString(word)}&t=KJV",
        "lookup_original_language"
    );

    return (config, tools);
}

// ── C# Troubleshooting ────────────────────────────────────────────────

public static (AgentConfig config, ToolRegistry tools) CreateCSharpAgent(
    string ollamaEndpoint = "http://localhost:11434",
    string? openAiKey = null)
{
    var config = new AgentConfig
    {
        OllamaEndpoint = ollamaEndpoint,
        OllamaModel = "deepseek-coder:6.7b",   // code-specialist model
        CloudModel = "gpt-4o",
        OpenAiApiKey = openAiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
        MaxToolRounds = 10,
        SystemPrompt = """
                You are a senior C# / .NET engineer with deep expertise in:
                - C# 12, .NET 8, ASP.NET Core, Entity Framework Core
                - Async/await patterns, TPL, and concurrent programming
                - Dependency injection, SOLID principles, clean architecture
                - Performance profiling, memory management, and garbage collection
                - Common NuGet ecosystem (Serilog, Polly, MediatR, FluentValidation, etc.)

                Your approach:
                - Ask for the full exception message and stack trace if not provided
                - Ask for the relevant code snippet
                - Identify the root cause before suggesting fixes
                - Provide working code examples, not pseudo-code
                - Flag breaking changes between .NET versions when relevant
                - Suggest defensive patterns to prevent recurrence

                Format code with proper indentation. Prefer modern C# idioms.
                """,
    };

    var tools = new ToolRegistry();

    // Dotnet runtime info
    tools.Register(
        [Description("Returns the .NET runtime version currently installed on this machine.")]
    () => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        "get_dotnet_version"
    );

    // NuGet version lookup stub
    tools.Register(
        [Description("Looks up the latest stable version of a NuGet package.")]
    ([Description("Package name, e.g. 'Newtonsoft.Json'")] string packageName) =>
            $"Check the latest version of '{packageName}' at: " +
            $"https://www.nuget.org/packages/{Uri.EscapeDataString(packageName)}",
        "lookup_nuget_package"
    );

    // Common exception explainer
    tools.Register(
        [Description("Explains the common causes of a well-known .NET exception type.")]
    ([Description("Exception type name, e.g. 'NullReferenceException'")] string exceptionType) =>
        {
            var db = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NullReferenceException"] = "Accessing a member on a null object. Use null-conditional (?.) operator, null checks, or ensure initialization. Enable nullable reference types (#nullable enable) to catch these at compile time.",
                ["StackOverflowException"] = "Infinite or deeply nested recursion. Check base cases, convert to iterative, or increase stack size as a last resort.",
                ["InvalidOperationException"] = "Object is in an invalid state for the operation. Common causes: modifying a collection while iterating, calling async method incorrectly (.Result or .Wait() causing deadlock).",
                ["TaskCanceledException"] = "A CancellationToken was triggered before the Task completed. Ensure your CancellationTokenSource lifetime is correct and tokens are passed through the call chain.",
                ["ObjectDisposedException"] = "Using an object after Dispose() was called. Common with HttpClient, DbContext, or IMemoryCache — check DI lifetime (Singleton vs Scoped vs Transient).",
                ["OutOfMemoryException"] = "Process memory exhausted. Check for memory leaks (use dotnet-counters or Visual Studio Diagnostic Tools), large allocations, or LOH fragmentation.",
            };
            return db.TryGetValue(exceptionType, out var explanation)
                ? $"{exceptionType}: {explanation}"
                : $"No built-in explanation for '{exceptionType}'. Search docs.microsoft.com for details.";
        },
        "explain_exception"
    );

    // System diagnostics
    tools.RegisterDefaults(); // get_system_info, calculate, etc. are useful here too

    return (config, tools);
}
}