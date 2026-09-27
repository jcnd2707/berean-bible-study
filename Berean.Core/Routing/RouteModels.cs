namespace Berean.Core.Routing;

public enum QueryIntent { Verse, Definition, Conceptual, Mixed }

/// <summary>How a turn is answered. Quick skips retrieval; Compare sets the traditions side by side.</summary>
public enum QueryMode { Quick, Deep, Compare }

public sealed record RouteOptions(bool IncludeSda = false, QueryMode Mode = QueryMode.Deep);

/// <summary>
/// What the router retrieved for one turn. <see cref="Sources"/> lists exactly what the prompt
/// contains (after trimming), numbered S# for the main pass and A# for the Adventist pass.
/// </summary>
public sealed record RetrievalResult(
    QueryIntent Intent,
    VerseReference? VerseRef,
    FormattedContext? Context)
{
    /// <summary>Everything to put in the prompt (verse text, reference material, Adventist block).</summary>
    public string? Text => Context?.Text;
    public string? MainContext => Context?.Main;
    public string? AdventistContext => Context?.Adventist;
    public IReadOnlyList<ContextSource> Sources => Context?.Sources ?? [];
    public IReadOnlyList<ScoredChunk> Chunks => Sources.Select(s => s.Scored).ToList();
}
