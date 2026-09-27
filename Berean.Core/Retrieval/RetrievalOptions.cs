namespace Berean.Core.Retrieval;

/// <summary>
/// Configuration for the Bible study agent's retrieval.
/// Bound from appsettings.json under "Agents:BibleAgent".
/// </summary>
public class RetrievalOptions
{
    public string RagDbPath { get; set; } = "index/agent.rag.db";

    /// <summary>
    /// Default language for retrieval filtering ("en" or "es").
    /// Can be overridden per-connection at runtime via the UI language selector.
    /// </summary>
    public string Language { get; set; } = "en";

    /// <summary>Base URL of the BereanResource.Api (e.g. "http://localhost:5121"). Required.</summary>
    public string ResourceApiBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Bible module IDs whose exact verse text is fetched for a passage (and available to the
    /// lookup tools). Bibles are never embedded. Example: ["akjvstrong", "KJV"]
    /// </summary>
    public List<string> AllowedBibleModules { get; set; } = [];

    // ── Indexing ───────────────────────────────────────────────────────────
    /// <summary>
    /// Commentary module IDs to include in the vector index. Empty (or omitted) means all.
    /// Verse questions read every commentary directly from the API regardless.
    /// </summary>
    public List<string> AllowedCommentaryModules { get; set; } = [];
    public int ChunkSize { get; set; } = 200;
    public int ChunkOverlap { get; set; } = 50;

    /// <summary>
    /// Commentaries are much larger than books (hundreds of thousands of chunks at 400
    /// characters), and embedding runs at only a few chunks a second on CPU, so they use
    /// larger chunks.
    /// </summary>
    public int CommentaryChunkSize { get; set; } = 1200;
    public int CommentaryChunkOverlap { get; set; } = 150;

    /// <summary>
    /// Index commentaries and books that are in the library but not yet in the index, in the
    /// background at startup. Off by default: a full commentary can take hours to embed, so
    /// this is something to turn on deliberately (the pending modules are logged and shown in
    /// the index status either way).
    /// </summary>
    public bool AutoIndexMissingModules { get; set; } = false;

    // ── MMR tuning ─────────────────────────────────────────────────────────
    public int TopK { get; set; } = 8;
    public float MmrLambda { get; set; } = 0.6f;
    public int MmrCandidateK { get; set; } = 80;

    // ── Neutrality ─────────────────────────────────────────────────────────
    /// <summary>Most chunks any one module may contribute to an answer (0 = unlimited).</summary>
    public int MaxPerModule { get; set; } = 2;

    /// <summary>How many chunks the separate Adventist pass returns (only when the SDA toggle is on).</summary>
    public int AdventistTopK { get; set; } = 4;

    /// <summary>Compare mode: chunks per tradition, and the similarity a tradition must reach to get a section.</summary>
    public int CompareChunksPerTradition { get; set; } = 2;
    public float CompareMinScore { get; set; } = 0.45f;

    /// <summary>Rough cap on retrieved material in the prompt (lowest-ranked chunks are dropped first).</summary>
    public int MaxContextTokens { get; set; } = 12000;

    /// <summary>Most characters taken from any single commentary entry.</summary>
    public int MaxEntryChars { get; set; } = 1500;
}
