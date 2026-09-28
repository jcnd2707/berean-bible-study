using System.Reflection;

namespace Berean.Core.Agent;

/// <summary>
/// The prompts, kept as markdown files (Agents/Prompts/*.md, embedded in the assembly) so they
/// are easy to read and edit outside C# string literals.
/// </summary>
public static class Prompts
{
    public static string System => Load("system");

    /// <summary>Added to the user turn (not the system prompt) once per selected perspective.</summary>
    public static string PerspectiveAddendum => Load("perspective-addendum");

    /// <summary>Added to the user turn in Compare mode.</summary>
    public static string CompareAddendum => Load("compare-addendum");

    /// <summary>System prompt for the one-shot recap call that ends a full session (Phase 5).</summary>
    public static string Recap => Load("recap");

    private static readonly Dictionary<string, string> Cache = [];

    private static string Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;

            var assembly = typeof(Prompts).Assembly;
            var resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($".Prompts.{name}.md", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Embedded prompt '{name}.md' not found.");

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            Cache[name] = text;
            return text;
        }
    }
}
