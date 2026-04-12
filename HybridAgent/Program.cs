//using HybridAgent.Core;
//using HybridAgent.Core.Agents;
//using HybridAgent.Core.Models;
//using Microsoft.Extensions.Logging;

//var logFactory = LoggerFactory.Create(b => b
//    .AddConsole()
//    .SetMinimumLevel(LogLevel.Information));

//Console.WriteLine("╔══════════════════════════════════════════════════════╗");
//Console.WriteLine("║         Hybrid AI Agent  —  Multi-Domain             ║");
//Console.WriteLine("╚══════════════════════════════════════════════════════╝");
//Console.WriteLine();
//Console.WriteLine("  Select an agent:");
//Console.WriteLine("  [1] Car Diagnostics      (llama3.2:3b)");
//Console.WriteLine("  [2] Bible Research       (llama3.2:3b)");
//Console.WriteLine("  [3] C# Troubleshooting   (deepseek-coder:6.7b)");
//Console.WriteLine();
//Console.Write("Choice (1/2/3): ");

//var choice = Console.ReadLine()?.Trim();

//var (config, tools) = choice switch
//{
//    "1" => AgentFactory.CreateCarAgent(),
//    "2" => AgentFactory.CreateBibleAgent(),
//    "3" => AgentFactory.CreateCSharpAgent(),
//    _ => AgentFactory.CreateCarAgent()
//};

//config.RagDocsDirectory = choice switch
//{
//    "1" => "docs/car",
//    "2" => "D:\\Bible Study\\bible-docs",
//    "3" => "docs/csharp",
//    _ => "docs/car"
//};
//config.RagIndexPath = choice switch
//{
//    "1" => "index/car.json",
//    "2" => "D:\\Bible Study\\index/bible.json",
//    "3" => "index/csharp.json",
//    _ => "index/car.json"
//};

//Directory.CreateDirectory(config.RagDocsDirectory);
//Directory.CreateDirectory("index");

//string agentName = choice switch
//{
//    "1" => "Car Diagnostics",
//    "2" => "Bible Research",
//    "3" => "C# Troubleshooting",
//    _ => "Car Diagnostics"
//};

//Console.WriteLine();
//Console.WriteLine($"  Agent : {agentName}  ({config.OllamaModel})");
//Console.WriteLine($"  Docs  : {config.RagDocsDirectory}");

//Console.Write("  Cloud : ");
//if (!string.IsNullOrWhiteSpace(config.OpenAiApiKey))
//{
//    Console.ForegroundColor = ConsoleColor.Green;
//    Console.WriteLine($"{config.CloudModel} (available — type 'verdict' to use)");
//}
//else
//{
//    Console.ForegroundColor = ConsoleColor.Yellow;
//    Console.WriteLine("not configured — set OPENAI_API_KEY to enable");
//}
//Console.ResetColor();
//Console.WriteLine();

//Console.Write("  Initializing...");
//var pipeline = await HybridPipeline.CreateAsync(config, tools, logFactory);
//Console.WriteLine(" ready.\n");

//// ── Commands ───────────────────────────────────────────────────────────────

//Console.WriteLine($"  {agentName} agent ready.");

//if (pipeline.CloudAvailable)
//    Console.WriteLine("  Commands: 'verdict' = cloud final answer | 'reset' = new topic | 'exit'");
//else
//    Console.WriteLine("  Commands: 'reset' = new topic | 'exit'");

//Console.WriteLine();

//// ── Chat loop ──────────────────────────────────────────────────────────────

//while (true)
//{
//    Console.ForegroundColor = ConsoleColor.Cyan;
//    Console.Write("You: ");
//    Console.ResetColor();

//    var input = Console.ReadLine()?.Trim();
//    if (string.IsNullOrEmpty(input)) continue;

//    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
//        break;

//    if (input.Equals("reset", StringComparison.OrdinalIgnoreCase))
//    {
//        pipeline.Reset();
//        Console.ForegroundColor = ConsoleColor.DarkGray;
//        Console.WriteLine("  [Conversation reset]\n");
//        Console.ResetColor();
//        continue;
//    }

//    if (input.Equals("verdict", StringComparison.OrdinalIgnoreCase))
//    {
//        if (!pipeline.CloudAvailable)
//        {
//            Console.ForegroundColor = ConsoleColor.Yellow;
//            Console.WriteLine("  [Cloud not configured — set OPENAI_API_KEY and restart]\n");
//            Console.ResetColor();
//            continue;
//        }

//        Console.ForegroundColor = ConsoleColor.DarkGray;
//        Console.WriteLine($"  [Escalating to {config.CloudModel}...]\n");
//        Console.ResetColor();

//        try
//        {
//            var result = await pipeline.GetVerdictAsync();
//            result?.PrintToConsole();
//        }
//        catch (Exception ex)
//        {
//            Console.ForegroundColor = ConsoleColor.Red;
//            Console.WriteLine($"  [Cloud error] {ex.Message}");
//            Console.ResetColor();
//        }
//        continue;
//    }

//    // Normal chat — local Ollama, full memory
//    try
//    {
//        var reply = await pipeline.ChatAsync(input);
//        Console.ForegroundColor = ConsoleColor.Green;
//        Console.Write("Agent: ");
//        Console.ResetColor();
//        Console.WriteLine(reply);
//        Console.WriteLine();
//    }
//    catch (Exception ex)
//    {
//        Console.ForegroundColor = ConsoleColor.Red;
//        Console.WriteLine($"  [Error] {ex.GetType().Name}: {ex.Message}");
//        Console.ResetColor();

//        if (ex.Message.Contains("connect", StringComparison.OrdinalIgnoreCase))
//            Console.WriteLine("  → Is Ollama running? Try: ollama serve");
//    }
//}

//Console.WriteLine("\nGoodbye!");