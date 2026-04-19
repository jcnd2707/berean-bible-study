namespace HybridAgent.Core.RAG;

/// <summary>
/// Configuration for one agent's RAG pipeline.
/// Bound from appsettings.json under "Agents:{AgentName}".
/// </summary>
public class AgentRagConfig
{
    public string RagDbPath { get; set; } = "index/agent.rag.db";
    public string ModulesRootPath { get; set; } = string.Empty;
    public List<string> AllowedExtensions { get; set; } = [];

    /// <summary>
    /// Default language for retrieval filtering ("en" or "es").
    /// Can be overridden per-connection at runtime via the UI language selector.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Path to the folder containing dictionary files (.dctx, .lexx).
    /// Dictionaries are NOT embedded — they are queried directly as tools.
    /// If empty, dictionary tools are disabled.
    /// </summary>
    public string DictionaryRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the BereanResource.Api (e.g. "http://localhost:5000").
    /// When set, RAG indexing is driven by the API instead of local e-Sword files.
    /// </summary>
    public string ResourceApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Bible module IDs available for on-demand verse lookup via the lookup_verse tool.
    /// Bibles are NOT embedded into the vector store — they are fetched per-request.
    /// Example: ["KJV", "NKJV"]
    /// </summary>
    public List<string> AllowedBibleModules { get; set; } = [];

    // ── MMR tuning ─────────────────────────────────────────────────────────
    public int TopK { get; set; } = 8;
    public float MmrLambda { get; set; } = 0.6f;
    public int MmrCandidateK { get; set; } = 80;

    // ── Helpers ────────────────────────────────────────────────────────────

    public IEnumerable<string> ResolveFiles()
    {
        if (string.IsNullOrWhiteSpace(ModulesRootPath) || !Directory.Exists(ModulesRootPath))
            return [];

        var extensions = AllowedExtensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .ToHashSet();

        if (extensions.Count == 0) return [];

        return Directory
            .EnumerateFiles(ModulesRootPath, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
    }

    /// <summary>
    /// Returns all .dctx and .lexx files under DictionaryRootPath.
    /// </summary>
    public IEnumerable<string> ResolveDictionaryFiles()
    {
        if (string.IsNullOrWhiteSpace(DictionaryRootPath) ||
            !Directory.Exists(DictionaryRootPath))
            return [];

        return Directory
            .EnumerateFiles(DictionaryRootPath, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".dctx" or ".lexx";
            });
    }
}