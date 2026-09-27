namespace BereanResourceApi.Models;

/// <summary>
/// Tradition metadata for one module, bound from the "ModuleProfiles" config section
/// (keyed by module id). Tradition is one of <see cref="ModuleProfileTraditions.All"/>.
/// </summary>
public class ModuleProfile
{
    public string Tradition { get; set; } = ModuleProfileTraditions.Unclassified;
    public string? Era { get; set; }

    /// <summary>Short name used when citing the module (e.g. "Barnes' Notes on the Bible").</summary>
    public string? DisplayName { get; set; }
}

public static class ModuleProfileTraditions
{
    public const string Unclassified = "Unclassified";

    public static readonly IReadOnlyList<string> All =
    [
        "Adventist", "Reformed", "Wesleyan", "Baptist", "Lutheran", "Evangelical",
        "Catholic", "Orthodox", "Jewish", "Academic", "Lexical", Unclassified,
    ];

    public static string Normalise(string? value) =>
        All.FirstOrDefault(t => t.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Unclassified;
}

/// <summary>A module that has no entry under "ModuleProfiles" (to be labelled by the user).</summary>
public record UnclassifiedModule(string Kind, string ModuleId, string Name);
