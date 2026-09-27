namespace Berean.Core.Retrieval;

/// <summary>A chunk with its similarity to the query (0 for exact, non-semantic lookups).</summary>
public sealed record ScoredChunk(DocumentChunk Chunk, float Score);

/// <summary>
/// Which chunks a search may return. Replaces the separate sourceType/language arguments and
/// adds tradition filtering and a per-module cap.
/// </summary>
public sealed record RetrievalFilter
{
    public IReadOnlyList<SourceType>? SourceTypes { get; init; }
    public string? Language { get; init; }

    /// <summary>When set, only chunks whose tradition is in this list are returned.</summary>
    public IReadOnlyList<string>? IncludeTraditions { get; init; }

    /// <summary>Chunks whose tradition is in this list are never returned.</summary>
    public IReadOnlyList<string>? ExcludeTraditions { get; init; }

    /// <summary>
    /// Most chunks any one module may contribute to a result (0 = unlimited). Large commentaries
    /// would otherwise dominate by sheer size, which is a bias of its own.
    /// </summary>
    public int MaxPerModule { get; init; } = 2;

    /// <summary>Chunks per module already used elsewhere in the same answer (counts toward the cap).</summary>
    public IReadOnlyDictionary<string, int>? ExistingPerModule { get; init; }

    /// <summary>Semantic results below this similarity are dropped (0 = keep everything).</summary>
    public float MinScore { get; init; }

    public bool Matches(DocumentChunk c)
    {
        if (SourceTypes is not null && !SourceTypes.Contains(c.SourceType)) return false;
        if (Language is not null && c.Language != Language) return false;

        // Rows not yet tagged count as Unclassified.
        var tradition = c.Tradition ?? Traditions.Unclassified;
        if (IncludeTraditions is not null && !IncludeTraditions.Contains(tradition)) return false;
        if (ExcludeTraditions is not null && ExcludeTraditions.Contains(tradition)) return false;
        return true;
    }
}
