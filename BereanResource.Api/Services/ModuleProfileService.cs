using BereanResourceApi.Models;

namespace BereanResourceApi.Services;

/// <summary>
/// Looks up tradition metadata for modules and stamps it onto the resource DTOs so both the
/// agent (retrieval filtering) and the web UI (source chips) can use it.
/// </summary>
public class ModuleProfileService
{
    private readonly Dictionary<string, ModuleProfile> _profiles;

    public ModuleProfileService(IConfiguration config, ILogger<ModuleProfileService> logger)
    {
        _profiles = new(StringComparer.OrdinalIgnoreCase);

        var section = config.GetSection("ModuleProfiles");
        foreach (var child in section.GetChildren())
        {
            if (child.Key.StartsWith('_')) continue; // comments
            var profile = child.Get<ModuleProfile>() ?? new ModuleProfile();
            var normalised = ModuleProfileTraditions.Normalise(profile.Tradition);
            if (!normalised.Equals(profile.Tradition, StringComparison.Ordinal))
                logger.LogWarning("ModuleProfiles:{Key} has unknown tradition '{Value}' — using {Used}",
                    child.Key, profile.Tradition, normalised);
            profile.Tradition = normalised;
            _profiles[child.Key] = profile;
        }
    }

    public bool HasProfile(string moduleId) => _profiles.ContainsKey(moduleId);

    public ModuleProfile Get(string moduleId) =>
        _profiles.TryGetValue(moduleId, out var p) ? p : new ModuleProfile();

    public ResourceModule Apply(ResourceModule m)
    {
        var p = Get(m.ModuleId);
        return m with { Tradition = p.Tradition, Era = p.Era, DisplayName = p.DisplayName ?? m.Name };
    }

    public List<ResourceModule> Apply(IEnumerable<ResourceModule> modules) =>
        modules.Select(Apply).ToList();

    public BookSummary Apply(BookSummary b)
    {
        var p = Get(b.ModuleId);
        return b with { Tradition = p.Tradition, Era = p.Era };
    }

    public List<BookSummary> Apply(IEnumerable<BookSummary> books) =>
        books.Select(Apply).ToList();
}
