namespace Berean.Core.Routing;

public record VerseReference(
    int BookNumber,
    int Chapter,
    int? Verse,
    int? VerseEnd,
    List<int>? Verses,
    string OriginalText)
{
    public bool IsRange => Verse.HasValue && VerseEnd.HasValue;
    public bool IsDiscrete => Verses is { Count: > 1 };
    public bool IsSingle => !IsRange && !IsDiscrete;

    public string VerseDisplay => this switch
    {
        { IsRange: true } => $"{Chapter}:{Verse}-{VerseEnd}",
        { IsDiscrete: true } => $"{Chapter}:{string.Join(",", Verses!)}",
        { Verse: not null } => $"{Chapter}:{Verse}",
        _ => $"{Chapter}"
    };
}
