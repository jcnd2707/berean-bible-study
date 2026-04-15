namespace HybridAgent.Core.RAG;

/// <summary>
/// Maps Bible book names and common abbreviations to e-Sword book numbers (1–66).
/// Covers English and Spanish names/abbreviations so the pre-router works for both languages.
/// </summary>
public static class BibleBookMap
{
    // e-Sword book numbers: 1=Genesis … 39=Malachi, 40=Matthew … 66=Revelation
    private static readonly Dictionary<string, int> _map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Old Testament ──────────────────────────────────────────────────
            // Genesis
            ["genesis"] = 1,
            ["gen"] = 1,
            ["ge"] = 1,
            ["gn"] = 1,
            ["génesis"] = 1,
            // Exodus
            ["exodus"] = 2,
            ["exo"] = 2,
            ["ex"] = 2,
            ["éxodo"] = 2,
            ["exodo"] = 2,
            // Leviticus
            ["leviticus"] = 3,
            ["lev"] = 3,
            ["le"] = 3,
            ["lv"] = 3,
            ["levítico"] = 3,
            ["levitico"] = 3,
            // Numbers
            ["numbers"] = 4,
            ["num"] = 4,
            ["nu"] = 4,
            ["nm"] = 4,
            ["números"] = 4,
            ["numeros"] = 4,
            // Deuteronomy
            ["deuteronomy"] = 5,
            ["deut"] = 5,
            ["dt"] = 5,
            ["deuteronomio"] = 5,
            // Joshua
            ["joshua"] = 6,
            ["josh"] = 6,
            ["jos"] = 6,
            ["josué"] = 6,
            ["josue"] = 6,
            // Judges
            ["judges"] = 7,
            ["judg"] = 7,
            ["jdg"] = 7,
            ["jueces"] = 7,
            ["jue"] = 7,
            // Ruth
            ["ruth"] = 8,
            ["ru"] = 8,
            ["rut"] = 8,
            // 1 Samuel
            ["1 samuel"] = 9,
            ["1samuel"] = 9,
            ["1sam"] = 9,
            ["1sa"] = 9,
            ["1 samuel"] = 9,
            // 2 Samuel
            ["2 samuel"] = 10,
            ["2samuel"] = 10,
            ["2sam"] = 10,
            ["2sa"] = 10,
            // 1 Kings
            ["1 kings"] = 11,
            ["1kings"] = 11,
            ["1ki"] = 11,
            ["1kgs"] = 11,
            ["1 reyes"] = 11,
            ["1reyes"] = 11,
            ["1re"] = 11,
            // 2 Kings
            ["2 kings"] = 12,
            ["2kings"] = 12,
            ["2ki"] = 12,
            ["2kgs"] = 12,
            ["2 reyes"] = 12,
            ["2reyes"] = 12,
            ["2re"] = 12,
            // 1 Chronicles
            ["1 chronicles"] = 13,
            ["1chr"] = 13,
            ["1ch"] = 13,
            ["1 crónicas"] = 13,
            ["1cronicas"] = 13,
            ["1cr"] = 13,
            // 2 Chronicles
            ["2 chronicles"] = 14,
            ["2chr"] = 14,
            ["2ch"] = 14,
            ["2 crónicas"] = 14,
            ["2cronicas"] = 14,
            ["2cr"] = 14,
            // Ezra
            ["ezra"] = 15,
            ["ezr"] = 15,
            ["esdras"] = 15,
            ["esd"] = 15,
            // Nehemiah
            ["nehemiah"] = 16,
            ["neh"] = 16,
            ["nehemías"] = 16,
            ["nehemias"] = 16,
            // Esther
            ["esther"] = 17,
            ["est"] = 17,
            ["ester"] = 17,
            // Job
            ["job"] = 18,
            // Psalms
            ["psalms"] = 19,
            ["psalm"] = 19,
            ["ps"] = 19,
            ["psa"] = 19,
            ["salmos"] = 19,
            ["sal"] = 19,
            // Proverbs
            ["proverbs"] = 20,
            ["prov"] = 20,
            ["pr"] = 20,
            ["proverbios"] = 20,
            ["pro"] = 20,
            // Ecclesiastes
            ["ecclesiastes"] = 21,
            ["eccl"] = 21,
            ["ec"] = 21,
            ["eclesiastés"] = 21,
            ["eclesiastes"] = 21,
            // Song of Solomon
            ["song of solomon"] = 22,
            ["song"] = 22,
            ["ss"] = 22,
            ["sos"] = 22,
            ["cantares"] = 22,
            ["cantar"] = 22,
            ["cnt"] = 22,
            // Isaiah
            ["isaiah"] = 23,
            ["isa"] = 23,
            ["isaías"] = 23,
            ["isaias"] = 23,
            // Jeremiah
            ["jeremiah"] = 24,
            ["jer"] = 24,
            ["jeremías"] = 24,
            ["jeremias"] = 24,
            // Lamentations
            ["lamentations"] = 25,
            ["lam"] = 25,
            ["lamentaciones"] = 25,
            // Ezekiel
            ["ezekiel"] = 26,
            ["ezek"] = 26,
            ["eze"] = 26,
            ["ezequiel"] = 26,
            ["eze"] = 26,
            // Daniel
            ["daniel"] = 27,
            ["dan"] = 27,
            ["da"] = 27,
            // Hosea
            ["hosea"] = 28,
            ["hos"] = 28,
            ["oseas"] = 28,
            ["os"] = 28,
            // Joel
            ["joel"] = 29,
            ["joe"] = 29,
            ["jl"] = 29,
            // Amos
            ["amos"] = 30,
            ["am"] = 30,
            ["amós"] = 30,
            // Obadiah
            ["obadiah"] = 31,
            ["obad"] = 31,
            ["ob"] = 31,
            ["abdías"] = 31,
            ["abdias"] = 31,
            // Jonah
            ["jonah"] = 32,
            ["jon"] = 32,
            ["jonás"] = 32,
            ["jonas"] = 32,
            // Micah
            ["micah"] = 33,
            ["mic"] = 33,
            ["miqueas"] = 33,
            // Nahum
            ["nahum"] = 34,
            ["nah"] = 34,
            // Habakkuk
            ["habakkuk"] = 35,
            ["hab"] = 35,
            ["habacuc"] = 35,
            // Zephaniah
            ["zephaniah"] = 36,
            ["zeph"] = 36,
            ["zep"] = 36,
            ["sofonías"] = 36,
            ["sofonias"] = 36,
            // Haggai
            ["haggai"] = 37,
            ["hag"] = 37,
            ["hageo"] = 37,
            // Zechariah
            ["zechariah"] = 38,
            ["zech"] = 38,
            ["zec"] = 38,
            ["zacarías"] = 38,
            ["zacarias"] = 38,
            // Malachi
            ["malachi"] = 39,
            ["mal"] = 39,
            ["malaquías"] = 39,
            ["malaquias"] = 39,

            // ── New Testament ──────────────────────────────────────────────────
            // Matthew
            ["matthew"] = 40,
            ["matt"] = 40,
            ["mt"] = 40,
            ["mateo"] = 40,
            ["mat"] = 40,
            // Mark
            ["mark"] = 41,
            ["mk"] = 41,
            ["marcos"] = 41,
            ["mr"] = 41,
            // Luke
            ["luke"] = 42,
            ["lk"] = 42,
            ["lucas"] = 42,
            ["lc"] = 42,
            // John
            ["john"] = 43,
            ["jn"] = 43,
            ["joh"] = 43,
            ["juan"] = 43,
            // Acts
            ["acts"] = 44,
            ["act"] = 44,
            ["ac"] = 44,
            ["hechos"] = 44,
            ["hch"] = 44,
            // Romans
            ["romans"] = 45,
            ["rom"] = 45,
            ["ro"] = 45,
            ["romanos"] = 45,
            // 1 Corinthians
            ["1 corinthians"] = 46,
            ["1cor"] = 46,
            ["1co"] = 46,
            ["1 corintios"] = 46,
            ["1corintios"] = 46,
            ["1co"] = 46,
            // 2 Corinthians
            ["2 corinthians"] = 47,
            ["2cor"] = 47,
            ["2co"] = 47,
            ["2 corintios"] = 47,
            ["2corintios"] = 47,
            // Galatians
            ["galatians"] = 48,
            ["gal"] = 48,
            ["ga"] = 48,
            ["gálatas"] = 48,
            ["galatas"] = 48,
            // Ephesians
            ["ephesians"] = 49,
            ["eph"] = 49,
            ["efesios"] = 49,
            ["ef"] = 49,
            // Philippians
            ["philippians"] = 50,
            ["phil"] = 50,
            ["php"] = 50,
            ["filipenses"] = 50,
            ["fil"] = 50,
            // Colossians
            ["colossians"] = 51,
            ["col"] = 51,
            ["colosenses"] = 51,
            // 1 Thessalonians
            ["1 thessalonians"] = 52,
            ["1thess"] = 52,
            ["1th"] = 52,
            ["1 tesalonicenses"] = 52,
            ["1ts"] = 52,
            // 2 Thessalonians
            ["2 thessalonians"] = 53,
            ["2thess"] = 53,
            ["2th"] = 53,
            ["2 tesalonicenses"] = 53,
            ["2ts"] = 53,
            // 1 Timothy
            ["1 timothy"] = 54,
            ["1tim"] = 54,
            ["1ti"] = 54,
            ["1 timoteo"] = 54,
            ["1tm"] = 54,
            // 2 Timothy
            ["2 timothy"] = 55,
            ["2tim"] = 55,
            ["2ti"] = 55,
            ["2 timoteo"] = 55,
            ["2tm"] = 55,
            // Titus
            ["titus"] = 56,
            ["tit"] = 56,
            ["tito"] = 56,
            // Philemon
            ["philemon"] = 57,
            ["phlm"] = 57,
            ["filemón"] = 57,
            ["filemon"] = 57,
            // Hebrews
            ["hebrews"] = 58,
            ["heb"] = 58,
            ["hebreos"] = 58,
            // James
            ["james"] = 59,
            ["jas"] = 59,
            ["santiago"] = 59,
            ["stg"] = 59,
            // 1 Peter
            ["1 peter"] = 60,
            ["1pet"] = 60,
            ["1pe"] = 60,
            ["1 pedro"] = 60,
            ["1p"] = 60,
            // 2 Peter
            ["2 peter"] = 61,
            ["2pet"] = 61,
            ["2pe"] = 61,
            ["2 pedro"] = 61,
            ["2p"] = 61,
            // 1 John
            ["1 john"] = 62,
            ["1jn"] = 62,
            ["1jo"] = 62,
            ["1 juan"] = 62,
            // 2 John
            ["2 john"] = 63,
            ["2jn"] = 63,
            ["2jo"] = 63,
            ["2 juan"] = 63,
            // 3 John
            ["3 john"] = 64,
            ["3jn"] = 64,
            ["3jo"] = 64,
            ["3 juan"] = 64,
            // Jude
            ["jude"] = 65,
            ["jud"] = 65,
            ["judas"] = 65,
            // Revelation
            ["revelation"] = 66,
            ["rev"] = 66,
            ["re"] = 66,
            ["apocalipsis"] = 66,
            ["ap"] = 66,
        };

    /// <summary>
    /// Try to resolve a book name or abbreviation to its e-Sword book number.
    /// Returns null if the name is not recognised.
    /// </summary>
    public static int? Resolve(string name) =>
        _map.TryGetValue(name.Trim(), out var n) ? n : null;

    /// <summary>All known aliases, for use in regex building.</summary>
    public static IEnumerable<string> AllAliases => _map.Keys;
}