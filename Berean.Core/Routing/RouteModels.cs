namespace Berean.Core.Routing;

public enum QueryIntent { Verse, Definition, Conceptual, Mixed }

/// <summary>How a turn is answered. Quick skips retrieval; Compare sets the traditions side by side.</summary>
public enum QueryMode { Quick, Deep, Compare }

/// <summary>
/// <paramref name="SelectedPerspectives"/> is the conversation's locked-in perspective selection
/// (0 or more, capped by "MaxPerspectivesPerQuestion" — see <see cref="RetrievalOptions"/>), not a
/// per-message choice.
/// </summary>
public sealed record RouteOptions(IReadOnlyList<Perspective> SelectedPerspectives, QueryMode Mode = QueryMode.Deep)
{
    public static RouteOptions None(QueryMode mode = QueryMode.Deep) => new([], mode);
}

/// <summary>
/// What the router retrieved for one turn. <see cref="Sources"/> lists exactly what the prompt
/// contains (after trimming), numbered S# for the main pass and with each perspective's own
/// citation prefix for its pass.
/// </summary>
public sealed record RetrievalResult(
    QueryIntent Intent,
    VerseReference? VerseRef,
    FormattedContext? Context)
{
    /// <summary>Everything to put in the prompt (verse text, reference material, perspective blocks).</summary>
    public string? Text => Context?.Text;
    public string? MainContext => Context?.Main;
    public IReadOnlyList<PerspectiveContext> PerspectiveContexts => Context?.Perspectives ?? [];
    public IReadOnlyList<ContextSource> Sources => Context?.Sources ?? [];
    public IReadOnlyList<ScoredChunk> Chunks => Sources.Select(s => s.Scored).ToList();
}
