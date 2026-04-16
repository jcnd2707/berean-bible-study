namespace BereanResourceApi.Mapping;

/// <summary>
/// Maps e-Sword / scrollmapper integer book numbers (1-66) to localised names,
/// abbreviations, and chapter counts.
///
/// The frontend always works with human-readable names; integers stay internal.
///
/// Scrollmapper uses Roman-numeral prefixes and some different titles:
///   "I Samuel", "II Samuel", "I Kings", "II Kings", "I Chronicles", etc.
///   "Song of Solomon" instead of "Song of Songs"
///   "Revelation of John" instead of "Revelation"
/// All of these are registered as aliases so URL params and cross-reference
/// lookups both resolve correctly regardless of which name variant is used.
/// </summary>
public static class BookMapper
{
    private record BookMeta(
        int Number,
        string EnName,
        string EnAbbr,
        string EsName,
        string EsAbbr,
        int Chapters,
        string ScrollmapperName  // exact name stored in scrollmapper _books tables
    );

    private static readonly List<BookMeta> _books =
    [
        new(1,  "Genesis",         "Gen",  "Génesis",          "Gn",   50,  "Genesis"),
        new(2,  "Exodus",          "Exo",  "Éxodo",            "Ex",   40,  "Exodus"),
        new(3,  "Leviticus",       "Lev",  "Levítico",         "Lv",   27,  "Leviticus"),
        new(4,  "Numbers",         "Num",  "Números",          "Nm",   36,  "Numbers"),
        new(5,  "Deuteronomy",     "Deu",  "Deuteronomio",     "Dt",   34,  "Deuteronomy"),
        new(6,  "Joshua",          "Jos",  "Josué",            "Jos",  24,  "Joshua"),
        new(7,  "Judges",          "Jdg",  "Jueces",           "Jue",  21,  "Judges"),
        new(8,  "Ruth",            "Rut",  "Rut",              "Rt",    4,  "Ruth"),
        new(9,  "1 Samuel",        "1Sa",  "1 Samuel",         "1Sa",  31,  "I Samuel"),
        new(10, "2 Samuel",        "2Sa",  "2 Samuel",         "2Sa",  24,  "II Samuel"),
        new(11, "1 Kings",         "1Ki",  "1 Reyes",          "1Re",  22,  "I Kings"),
        new(12, "2 Kings",         "2Ki",  "2 Reyes",          "2Re",  25,  "II Kings"),
        new(13, "1 Chronicles",    "1Ch",  "1 Crónicas",       "1Cr",  29,  "I Chronicles"),
        new(14, "2 Chronicles",    "2Ch",  "2 Crónicas",       "2Cr",  36,  "II Chronicles"),
        new(15, "Ezra",            "Ezr",  "Esdras",           "Esd",  10,  "Ezra"),
        new(16, "Nehemiah",        "Neh",  "Nehemías",         "Neh",  13,  "Nehemiah"),
        new(17, "Esther",          "Est",  "Ester",            "Est",  10,  "Esther"),
        new(18, "Job",             "Job",  "Job",              "Job",  42,  "Job"),
        new(19, "Psalms",          "Psa",  "Salmos",           "Sal", 150,  "Psalms"),
        new(20, "Proverbs",        "Pro",  "Proverbios",       "Pr",   31,  "Proverbs"),
        new(21, "Ecclesiastes",    "Ecc",  "Eclesiastés",      "Ec",   12,  "Ecclesiastes"),
        new(22, "Song of Solomon", "Sng",  "Cantares",         "Cnt",   8,  "Song of Solomon"),
        new(23, "Isaiah",          "Isa",  "Isaías",           "Is",   66,  "Isaiah"),
        new(24, "Jeremiah",        "Jer",  "Jeremías",         "Jr",   52,  "Jeremiah"),
        new(25, "Lamentations",    "Lam",  "Lamentaciones",    "Lm",    5,  "Lamentations"),
        new(26, "Ezekiel",         "Eze",  "Ezequiel",         "Ez",   48,  "Ezekiel"),
        new(27, "Daniel",          "Dan",  "Daniel",           "Dn",   12,  "Daniel"),
        new(28, "Hosea",           "Hos",  "Oseas",            "Os",   14,  "Hosea"),
        new(29, "Joel",            "Joe",  "Joel",             "Jl",    3,  "Joel"),
        new(30, "Amos",            "Amo",  "Amós",             "Am",    9,  "Amos"),
        new(31, "Obadiah",         "Oba",  "Abdías",           "Abd",   1,  "Obadiah"),
        new(32, "Jonah",           "Jon",  "Jonás",            "Jon",   4,  "Jonah"),
        new(33, "Micah",           "Mic",  "Miqueas",          "Mi",    7,  "Micah"),
        new(34, "Nahum",           "Nah",  "Nahúm",            "Nah",   3,  "Nahum"),
        new(35, "Habakkuk",        "Hab",  "Habacuc",          "Hab",   3,  "Habakkuk"),
        new(36, "Zephaniah",       "Zep",  "Sofonías",         "Sof",   3,  "Zephaniah"),
        new(37, "Haggai",          "Hag",  "Hageo",            "Hg",    2,  "Haggai"),
        new(38, "Zechariah",       "Zec",  "Zacarías",         "Zac",  14,  "Zechariah"),
        new(39, "Malachi",         "Mal",  "Malaquías",        "Mal",   4,  "Malachi"),
        new(40, "Matthew",         "Mat",  "Mateo",            "Mt",   28,  "Matthew"),
        new(41, "Mark",            "Mrk",  "Marcos",           "Mr",   16,  "Mark"),
        new(42, "Luke",            "Luk",  "Lucas",            "Lc",   24,  "Luke"),
        new(43, "John",            "Jhn",  "Juan",             "Jn",   21,  "John"),
        new(44, "Acts",            "Act",  "Hechos",           "Hch",  28,  "Acts"),
        new(45, "Romans",          "Rom",  "Romanos",          "Ro",   16,  "Romans"),
        new(46, "1 Corinthians",   "1Co",  "1 Corintios",      "1Co",  16,  "I Corinthians"),
        new(47, "2 Corinthians",   "2Co",  "2 Corintios",      "2Co",  13,  "II Corinthians"),
        new(48, "Galatians",       "Gal",  "Gálatas",          "Gal",   6,  "Galatians"),
        new(49, "Ephesians",       "Eph",  "Efesios",          "Ef",    6,  "Ephesians"),
        new(50, "Philippians",     "Php",  "Filipenses",       "Fil",   4,  "Philippians"),
        new(51, "Colossians",      "Col",  "Colosenses",       "Col",   4,  "Colossians"),
        new(52, "1 Thessalonians", "1Th",  "1 Tesalonicenses", "1Ts",   5,  "I Thessalonians"),
        new(53, "2 Thessalonians", "2Th",  "2 Tesalonicenses", "2Ts",   3,  "II Thessalonians"),
        new(54, "1 Timothy",       "1Ti",  "1 Timoteo",        "1Ti",   6,  "I Timothy"),
        new(55, "2 Timothy",       "2Ti",  "2 Timoteo",        "2Ti",   4,  "II Timothy"),
        new(56, "Titus",           "Tit",  "Tito",             "Tit",   3,  "Titus"),
        new(57, "Philemon",        "Phm",  "Filemón",          "Flm",   1,  "Philemon"),
        new(58, "Hebrews",         "Heb",  "Hebreos",          "Heb",  13,  "Hebrews"),
        new(59, "James",           "Jas",  "Santiago",         "Stg",   5,  "James"),
        new(60, "1 Peter",         "1Pe",  "1 Pedro",          "1Pe",   5,  "I Peter"),
        new(61, "2 Peter",         "2Pe",  "2 Pedro",          "2Pe",   3,  "II Peter"),
        new(62, "1 John",          "1Jn",  "1 Juan",           "1Jn",   5,  "I John"),
        new(63, "2 John",          "2Jn",  "2 Juan",           "2Jn",   1,  "II John"),
        new(64, "3 John",          "3Jn",  "3 Juan",           "3Jn",   1,  "III John"),
        new(65, "Jude",            "Jud",  "Judas",            "Jud",   1,  "Jude"),
        new(66, "Revelation",      "Rev",  "Apocalipsis",      "Ap",   22,  "Revelation of John"),
    ];

    // ── Lookup indexes ────────────────────────────────────────────────────────

    private static readonly Dictionary<int, BookMeta> _byNumber =
        _books.ToDictionary(b => b.Number);

    // Combined name→number index covering all variants
    private static readonly Dictionary<string, int> _nameIndex = BuildNameIndex();

    private static Dictionary<string, int> BuildNameIndex()
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in _books)
        {
            // Primary names
            TryAdd(index, b.EnName, b.Number);
            TryAdd(index, b.EsName, b.Number);
            TryAdd(index, b.EnAbbr, b.Number);
            TryAdd(index, b.EsAbbr, b.Number);
            TryAdd(index, b.ScrollmapperName, b.Number);

            // Numeric-prefix aliases: "1 Samuel" → "i samuel", "2 Kings" → "ii kings"
            var romanised = ToRomanPrefix(b.EnName);
            if (romanised is not null) TryAdd(index, romanised, b.Number);

            // Reverse: "I Samuel" → "1 samuel"
            var numbered = ToNumberPrefix(b.ScrollmapperName);
            if (numbered is not null) TryAdd(index, numbered, b.Number);

            // Common alternate titles
            if (b.Number == 22)
            {
                TryAdd(index, "Song of Songs", b.Number);
                TryAdd(index, "Canticles", b.Number);
                TryAdd(index, "Canticle of Canticles", b.Number);
            }
            if (b.Number == 66)
            {
                TryAdd(index, "Revelation of John", b.Number);
                TryAdd(index, "Apocalypse", b.Number);
            }
            if (b.Number == 19) TryAdd(index, "Psalm", b.Number);
        }

        return index;
    }

    private static void TryAdd(Dictionary<string, int> dict, string key, int value)
    {
        if (!string.IsNullOrWhiteSpace(key))
            dict.TryAdd(key.Trim(), value);
    }

    /// <summary>"1 Samuel" → "i samuel", "2 Kings" → "ii kings", "3 John" → "iii john"</summary>
    private static string? ToRomanPrefix(string name)
    {
        if (name.StartsWith("1 ")) return "i " + name[2..].ToLowerInvariant();
        if (name.StartsWith("2 ")) return "ii " + name[2..].ToLowerInvariant();
        if (name.StartsWith("3 ")) return "iii " + name[2..].ToLowerInvariant();
        return null;
    }

    /// <summary>"I Samuel" → "1 samuel", "II Kings" → "2 kings", "III John" → "3 john"</summary>
    private static string? ToNumberPrefix(string name)
    {
        if (name.StartsWith("III ")) return "3 " + name[4..].ToLowerInvariant();
        if (name.StartsWith("II ")) return "2 " + name[3..].ToLowerInvariant();
        if (name.StartsWith("I ")) return "1 " + name[2..].ToLowerInvariant();
        return null;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves any recognised book name or abbreviation (English, Spanish,
    /// numeric prefix, Roman prefix, scrollmapper variant) to a 1-66 book number.
    /// Returns null if not recognised.
    /// </summary>
    public static int? ToNumber(string bookName)
    {
        var key = bookName.Trim();
        return _nameIndex.TryGetValue(key, out var n) ? n : null;
    }

    public static string ToEnglishName(int number) =>
        _byNumber.TryGetValue(number, out var m) ? m.EnName : $"Book {number}";

    public static string ToSpanishName(int number) =>
        _byNumber.TryGetValue(number, out var m) ? m.EsName : $"Libro {number}";

    public static string ToName(int number, string language = "en") =>
        language.StartsWith("es", StringComparison.OrdinalIgnoreCase)
            ? ToSpanishName(number)
            : ToEnglishName(number);

    public static string ToAbbreviation(int number, string language = "en") =>
        _byNumber.TryGetValue(number, out var m)
            ? (language.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? m.EsAbbr : m.EnAbbr)
            : number.ToString();

    /// <summary>
    /// Returns the exact book name as stored in scrollmapper _books tables.
    /// Used by CrossReferenceService when querying from_book / to_book columns.
    /// </summary>
    public static string ToScrollmapperName(int number) =>
        _byNumber.TryGetValue(number, out var m) ? m.ScrollmapperName : ToEnglishName(number);

    public static int ChapterCount(int number) =>
        _byNumber.TryGetValue(number, out var m) ? m.Chapters : 0;

    public static IReadOnlyList<(int Number, string Name, string Abbreviation, int Chapters)>
        AllBooks(string language = "en") =>
        _books.Select(b => (
            b.Number,
            language.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? b.EsName : b.EnName,
            language.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? b.EsAbbr : b.EnAbbr,
            b.Chapters
        )).ToList();

    /// <summary>Builds a human-readable reference string e.g. "Genesis 1:1" or "Genesis 1:1-3".</summary>
    public static string ToReference(int book, int chapter, int verseBegin, int verseEnd = 0, string language = "en")
    {
        var name = ToName(book, language);
        return verseEnd > verseBegin
            ? $"{name} {chapter}:{verseBegin}-{verseEnd}"
            : $"{name} {chapter}:{verseBegin}";
    }
}