namespace Berean.Core.Retrieval;

/// <summary>
/// Fixed vocabulary for the tradition tag on every module and chunk.
///
/// Role matters for balance: only <em>interpretive</em> traditions count as
/// "a view on what the passage means". <c>Lexical</c> material describes the
/// text itself (word meanings), and <c>Unclassified</c> is a to-do marker.
/// </summary>
public static class Traditions
{
    public const string Adventist = "Adventist";
    public const string Reformed = "Reformed";
    public const string Wesleyan = "Wesleyan";
    public const string Baptist = "Baptist";
    public const string Lutheran = "Lutheran";
    public const string Evangelical = "Evangelical";
    public const string Catholic = "Catholic";
    public const string Orthodox = "Orthodox";
    public const string Jewish = "Jewish";
    public const string Academic = "Academic";
    public const string Lexical = "Lexical";
    public const string Unclassified = "Unclassified";

    public static readonly IReadOnlyList<string> All =
    [
        Adventist, Reformed, Wesleyan, Baptist, Lutheran, Evangelical,
        Catholic, Orthodox, Jewish, Academic, Lexical, Unclassified,
    ];

    /// <summary>Normalises a configured value to the fixed vocabulary (case-insensitive).</summary>
    public static string Normalise(string? value) =>
        All.FirstOrDefault(t => t.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Unclassified;

    /// <summary>A view on what a passage means (everything except Lexical and Unclassified).</summary>
    public static bool IsInterpretive(string? tradition) =>
        tradition is not null && tradition != Lexical && tradition != Unclassified
        && All.Contains(tradition);

    public static bool IsReference(string? tradition) => tradition == Lexical;
}
