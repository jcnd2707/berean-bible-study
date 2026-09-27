using Microsoft.Extensions.Logging;

namespace Berean.Core.Retrieval;

public enum ModuleKind { Commentary, Book, Dictionary }

/// <summary>One module from BereanResource.Api, with the tradition label from its ModuleProfile.</summary>
public sealed record ModuleInfo(
    ModuleKind Kind,
    string ModuleId,
    string Name,
    string DisplayName,
    string Tradition,
    string? Era,
    string Language = "en")
{
    /// <summary>"(Evangelical, 19th c.)" — the tag shown next to every source in the prompt.</summary>
    public string TraditionTag => string.IsNullOrEmpty(Era) ? $"({Tradition})" : $"({Tradition}, {Era})";
}

/// <summary>
/// Snapshot of the modules BereanResource.Api serves: what exists, and how each is labelled.
/// Loaded once at pipeline start and used by the indexer, backfill, router and formatter.
/// </summary>
public sealed class ModuleCatalog
{
    private const string BookChapterMarker = ", Chapter ";

    private readonly Dictionary<string, ModuleInfo> _byId = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ModuleInfo> Commentaries { get; }
    public IReadOnlyList<ModuleInfo> Books { get; }
    public IReadOnlyList<ModuleInfo> Dictionaries { get; }
    /// <summary>Book titles as they appear as the prefix of a book chunk's Source.</summary>
    private readonly List<(string Title, ModuleInfo Module)> _bookTitles = [];

    public IReadOnlyList<(string Title, ModuleInfo Module)> BookTitles => _bookTitles;

    public static ModuleCatalog Empty { get; } = new([], [], [], []);

    public ModuleCatalog(
        IEnumerable<ModuleInfo> commentaries,
        IEnumerable<ModuleInfo> books,
        IEnumerable<ModuleInfo> dictionaries,
        IEnumerable<(string Title, string ModuleId)> bookTitles)
    {
        Commentaries = commentaries.ToList();
        Books = books.ToList();
        Dictionaries = dictionaries.ToList();
        foreach (var m in Commentaries.Concat(Books).Concat(Dictionaries))
            _byId[m.ModuleId] = m;
        foreach (var (title, id) in bookTitles)
            if (_byId.TryGetValue(id, out var m)) _bookTitles.Add((title, m));
    }

    public static async Task<ModuleCatalog> LoadAsync(
        BereanResourceApiClient client, ILogger? log = null, CancellationToken ct = default)
    {
        var commentaries = await client.GetCommentariesAsync(ct);
        var books = await client.GetBooksAsync(ct);
        var dictionaries = await client.GetDictionariesAsync(ct);

        if (commentaries.Count == 0 && books.Count == 0)
            log?.LogWarning("[Catalog] No modules returned by the resource API — is it running?");

        static ModuleInfo Make(ModuleKind kind, ApiResourceModule m) => new(
            kind, m.ModuleId, m.Name,
            string.IsNullOrWhiteSpace(m.DisplayName) ? m.Name : m.DisplayName!,
            Traditions.Normalise(m.Tradition), m.Era,
            string.IsNullOrWhiteSpace(m.Language) ? "en" : m.Language);

        var bookInfos = books.Select(b => new ModuleInfo(
            ModuleKind.Book, b.ModuleId, b.Title, b.Title,
            Traditions.Normalise(b.Tradition), b.Era,
            string.IsNullOrWhiteSpace(b.Language) ? "en" : b.Language)).ToList();

        var catalog = new ModuleCatalog(
            commentaries.Select(m => Make(ModuleKind.Commentary, m)),
            bookInfos,
            dictionaries.Select(m => Make(ModuleKind.Dictionary, m)),
            books.Select(b => (b.Title, b.ModuleId)));

        var unclassified = catalog.Commentaries.Concat(catalog.Books)
            .Where(m => m.Tradition == Traditions.Unclassified).Select(m => m.ModuleId).ToList();
        if (unclassified.Count > 0)
            log?.LogWarning("[Catalog] {Count} module(s) have no tradition profile: {Ids}",
                unclassified.Count, string.Join(", ", unclassified));

        return catalog;
    }

    public ModuleInfo? Find(string? moduleId) =>
        moduleId is not null && _byId.TryGetValue(moduleId, out var m) ? m : null;

    /// <summary>Book module for a chunk Source of the form "&lt;Title&gt;, Chapter N — …".</summary>
    public ModuleInfo? FindBookBySource(string source)
    {
        foreach (var (title, module) in _bookTitles)
            if (source.StartsWith(title + BookChapterMarker, StringComparison.Ordinal))
                return module;
        return null;
    }

    /// <summary>The module a chunk belongs to (stored id first, then by Source).</summary>
    public ModuleInfo? Resolve(DocumentChunk c) =>
        Find(c.ModuleId)
        ?? (c.SourceType == SourceType.Book ? FindBookBySource(c.Source) : Find(c.Source));

    /// <summary>Stable key used for the MaxPerModule cap.</summary>
    public static string ModuleKey(DocumentChunk c)
    {
        if (!string.IsNullOrEmpty(c.ModuleId)) return c.ModuleId!;
        if (c.SourceType == SourceType.Book)
        {
            var i = c.Source.IndexOf(BookChapterMarker, StringComparison.Ordinal);
            if (i > 0) return c.Source[..i];
        }
        return c.Source;
    }
}
