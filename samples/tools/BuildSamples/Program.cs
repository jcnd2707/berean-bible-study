using Microsoft.Data.Sqlite;

// Builds samples/ from a real bible-docs library: a small, redistributable subset covering the
// chapters the eval harness uses, licensed per samples/README.md.
//
//   dotnet run --project samples/tools/BuildSamples -- --source "D:\Bible Study\bible-docs" [--output samples]
//
// Never touches the source library in place: everything it needs is copied to a scratch temp
// directory first, and all extraction reads from that copy.

var source = Arg(args, "--source") ?? throw new ArgumentException("--source <bible-docs root> is required.");
var repoRoot = FindRepoRoot();
var output = Path.Combine(repoRoot, Arg(args, "--output") ?? "samples");

var targets = new (int Book, string Name, int[] Chapters)[]
{
    (1, "Genesis", [1, 2]),
    (23, "Isaiah", [7]),
    (27, "Daniel", [8]),
    (43, "John", [1, 3]),
    (45, "Romans", [8, 14]),
    (48, "Galatians", [3]),
    (50, "Philippians", [2]),
    (51, "Colossians", [2]),
    (58, "Hebrews", [9]),
    (66, "Revelation", [13, 20]),
};
// Some dictionary entries (concordance-style Strong's entries for common words like "and") and
// some commentary entries run to hundreds of KB of legitimate content. Capped to keep the whole
// folder small, same idea as the live app's own MaxEntryChars/CommentaryChunkSize.
const int MaxDictionaryChars = 800;
const int MaxCommentaryChars = 2000;
const int MinLinkedWordLength = 6;   // English-dictionary linking: longer words are more distinctive
const int MaxLinkedDictionaryEntries = 200;   // eastons/smiths/webster: cap, keeping the most distinctive (longest) words
const int MinCrossReferenceVotes = 10;   // keep only the stronger links, or the sample balloons

var bookNames = targets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
var chaptersByBook = targets.ToDictionary(t => t.Name, t => t.Chapters.ToHashSet(), StringComparer.OrdinalIgnoreCase);
var chaptersByNumber = targets.ToDictionary(t => t.Book, t => t.Chapters.ToHashSet());

var scratch = Path.Combine(Path.GetTempPath(), $"berean-samples-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
Console.WriteLine($"Scratch copy: {scratch}");

try
{
    // ── Copy only the files this run needs, never touching the source in place ──────────────
    var toCopy = new[]
    {
        "Bibles/BSB.db", "Bibles/ASV.db", "Bibles/YLT.db", "Bibles/SpaRV.db", "Bibles/akjvstrong.bbl",
        "Commentaries/barnes.cmt", "Commentaries/clarke.cmt", "Commentaries/henry.cmt", "Commentaries/jfb.cmt",
        "Dictionaries/strong.dct", "Dictionaries/bdb.dct", "Dictionaries/eastons.dct",
        "Dictionaries/smiths.dct", "Dictionaries/webster.dct",
    };
    foreach (var rel in toCopy)
    {
        var from = Path.Combine(source, rel);
        var to = Path.Combine(scratch, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }
    for (var n = 0; n <= 6; n++)
    {
        var rel = $"cross_references/cross_references_{n}.db";
        var from = Path.Combine(source, rel);
        if (!File.Exists(from)) continue;
        var to = Path.Combine(scratch, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }
    Console.WriteLine($"Copied {toCopy.Length} file(s) plus any cross-reference shards to scratch.");

    // ── Extract ──────────────────────────────────────────────────────────────────────────────
    Directory.CreateDirectory(Path.Combine(output, "Bibles"));
    Directory.CreateDirectory(Path.Combine(output, "Commentaries"));
    Directory.CreateDirectory(Path.Combine(output, "Dictionaries"));
    Directory.CreateDirectory(Path.Combine(output, "cross_references"));

    var strongsNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var chapterWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var module in new[] { "BSB", "ASV", "YLT", "SpaRV" })
        ExtractScrollmapperBible(
            Path.Combine(scratch, "Bibles", $"{module}.db"),
            Path.Combine(output, "Bibles", $"{module}.db"),
            chapterWords);

    ExtractMySwordBible(
        Path.Combine(scratch, "Bibles", "akjvstrong.bbl"),
        Path.Combine(output, "Bibles", "akjvstrong.bbl"),
        strongsNumbers);

    foreach (var module in new[] { "barnes", "clarke", "henry", "jfb" })
        ExtractCommentary(
            Path.Combine(scratch, "Commentaries", $"{module}.cmt"),
            Path.Combine(output, "Commentaries", $"{module}.cmt"),
            chapterWords);

    ExtractDictionaryByWords(
        Path.Combine(scratch, "Dictionaries", "strong.dct"),
        Path.Combine(output, "Dictionaries", "strong.dct"),
        strongsNumbers, matchAll: false);
    ExtractDictionaryByWords(
        Path.Combine(scratch, "Dictionaries", "bdb.dct"),
        Path.Combine(output, "Dictionaries", "bdb.dct"),
        strongsNumbers, matchAll: false);

    foreach (var module in new[] { "eastons", "smiths", "webster" })
        ExtractDictionaryByWords(
            Path.Combine(scratch, "Dictionaries", $"{module}.dct"),
            Path.Combine(output, "Dictionaries", $"{module}.dct"),
            chapterWords, matchAll: false, MaxLinkedDictionaryEntries);

    ExtractCrossReferences(scratch, Path.Combine(output, "cross_references", "cross_references_0.db"));

    Console.WriteLine("Done.");
    foreach (var f in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        if (!f.Contains($"{Path.DirectorySeparatorChar}tools{Path.DirectorySeparatorChar}"))
            Console.WriteLine($"  {Path.GetRelativePath(output, f)}  ({new FileInfo(f).Length / 1024} KB)");
}
finally
{
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(scratch, recursive: true); } catch { /* best effort */ }
}

// ── Bible extraction ─────────────────────────────────────────────────────────────────────────

void ExtractScrollmapperBible(string srcPath, string dstPath, HashSet<string> collectWords)
{
    using var src = OpenReadOnly(srcPath);
    var module = ScalarString(src, "SELECT translation FROM translations LIMIT 1")!;

    File.Delete(dstPath);
    using var dst = OpenReadWrite(dstPath);
    Exec(dst, "CREATE TABLE translations (translation TEXT PRIMARY KEY, title TEXT, license TEXT)");
    Exec(dst, $"CREATE TABLE {module}_books (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT)");
    Exec(dst, $"CREATE TABLE {module}_verses (id INTEGER PRIMARY KEY AUTOINCREMENT, book_id INTEGER, chapter INTEGER, verse INTEGER, text TEXT)");

    CopyRows(src, dst, "SELECT translation, title, license FROM translations",
        "INSERT INTO translations (translation, title, license) VALUES ($0, $1, $2)");
    CopyRows(src, dst, $"SELECT id, name FROM {module}_books",
        $"INSERT INTO {module}_books (id, name) VALUES ($0, $1)");

    using var cmd = src.CreateCommand();
    cmd.CommandText = $"""
        SELECT v.book_id, b.name, v.chapter, v.verse, v.text
        FROM {module}_verses v JOIN {module}_books b ON b.id = v.book_id
        """;
    using var r = cmd.ExecuteReader();
    var count = 0;
    while (r.Read())
    {
        var bookId = r.GetInt64(0);
        var name = r.GetString(1);
        var chapter = r.GetInt32(2);
        if (!bookNames.Contains(name) || !chaptersByBook[name].Contains(chapter)) continue;

        var text = r.GetString(4);
        foreach (var w in ContentWords(text)) collectWords.Add(w);

        using var ins = dst.CreateCommand();
        ins.CommandText = $"INSERT INTO {module}_verses (id, book_id, chapter, verse, text) VALUES ($id, $b, $c, $v, $t)";
        ins.Parameters.AddWithValue("$id", ++count);
        ins.Parameters.AddWithValue("$b", bookId);
        ins.Parameters.AddWithValue("$c", chapter);
        ins.Parameters.AddWithValue("$v", r.GetInt32(3));
        ins.Parameters.AddWithValue("$t", text);
        ins.ExecuteNonQuery();
    }
    Console.WriteLine($"{module}: {count} verse(s)");
}

void ExtractMySwordBible(string srcPath, string dstPath, HashSet<string> collectStrongs)
{
    using var src = OpenReadOnly(srcPath);

    File.Delete(dstPath);
    using var dst = OpenReadWrite(dstPath);
    CopyTableSchemaAndAllRows(src, dst, "Details");
    Exec(dst, "CREATE TABLE \"Bible\" (\"Book\" INT,\"Chapter\" INT,\"Verse\" INT,\"Scripture\" TEXT)");

    using var cmd = src.CreateCommand();
    cmd.CommandText = "SELECT Book, Chapter, Verse, Scripture FROM Bible";
    using var r = cmd.ExecuteReader();
    var count = 0;
    var strongsTag = new System.Text.RegularExpressions.Regex(@"<[WG]?([HG]\d+)>", System.Text.RegularExpressions.RegexOptions.Compiled);
    while (r.Read())
    {
        var book = r.GetInt32(0);
        var chapter = r.GetInt32(1);
        if (!chaptersByNumber.TryGetValue(book, out var chapters) || !chapters.Contains(chapter)) continue;

        var scripture = r.GetString(3);
        foreach (System.Text.RegularExpressions.Match m in strongsTag.Matches(scripture))
            collectStrongs.Add(m.Groups[1].Value);

        using var ins = dst.CreateCommand();
        ins.CommandText = "INSERT INTO \"Bible\" (Book, Chapter, Verse, Scripture) VALUES ($b, $c, $v, $s)";
        ins.Parameters.AddWithValue("$b", book);
        ins.Parameters.AddWithValue("$c", chapter);
        ins.Parameters.AddWithValue("$v", r.GetInt32(2));
        ins.Parameters.AddWithValue("$s", scripture);
        ins.ExecuteNonQuery();
        count++;
    }
    Console.WriteLine($"akjvstrong: {count} verse(s), {collectStrongs.Count} Strong's number(s) referenced");
}

// ── Commentary extraction ────────────────────────────────────────────────────────────────────

void ExtractCommentary(string srcPath, string dstPath, HashSet<string> collectWords)
{
    using var src = OpenReadOnly(srcPath);

    File.Delete(dstPath);
    using var dst = OpenReadWrite(dstPath);
    CopyTableSchemaAndAllRows(src, dst, "details");
    Exec(dst, "CREATE TABLE commentary(id INTEGER primary key autoincrement, book INTEGER, chapter INTEGER, fromverse INTEGER, toverse INTEGER, data TEXT)");

    using var cmd = src.CreateCommand();
    cmd.CommandText = "SELECT book, chapter, fromverse, toverse, data FROM commentary";
    using var r = cmd.ExecuteReader();
    var count = 0;
    while (r.Read())
    {
        var book = r.GetInt32(0);
        var chapter = r.GetInt32(1);
        if (!chaptersByNumber.TryGetValue(book, out var chapters) || !chapters.Contains(chapter)) continue;

        var data = r.IsDBNull(4) ? "" : r.GetString(4);
        foreach (var w in ContentWords(StripHtml(data))) collectWords.Add(w);
        var stored = Truncate(data, MaxCommentaryChars);

        using var ins = dst.CreateCommand();
        ins.CommandText = "INSERT INTO commentary (id, book, chapter, fromverse, toverse, data) VALUES ($id, $b, $c, $fv, $tv, $d)";
        ins.Parameters.AddWithValue("$id", ++count);
        ins.Parameters.AddWithValue("$b", book);
        ins.Parameters.AddWithValue("$c", chapter);
        ins.Parameters.AddWithValue("$fv", r.GetInt32(2));
        ins.Parameters.AddWithValue("$tv", r.GetInt32(3));
        ins.Parameters.AddWithValue("$d", stored);
        ins.ExecuteNonQuery();
    }
    Console.WriteLine($"{Path.GetFileNameWithoutExtension(srcPath)}: {count} entrie(s)");
}

// ── Dictionary extraction ────────────────────────────────────────────────────────────────────

void ExtractDictionaryByWords(string srcPath, string dstPath, HashSet<string> keep, bool matchAll, int? maxEntries = null)
{
    using var src = OpenReadOnly(srcPath);

    File.Delete(dstPath);
    using var dst = OpenReadWrite(dstPath);
    CopyTableSchemaAndAllRows(src, dst, "details");

    var hasOrder = TableHasColumn(src, "dictionary", "relativeorder");
    Exec(dst, hasOrder
        ? "CREATE TABLE \"dictionary\"(relativeorder INTEGER, word TEXT primary key collate nocase, data TEXT)"
        : "CREATE TABLE dictionary(word TEXT primary key collate nocase, data TEXT)");

    using var cmd = src.CreateCommand();
    cmd.CommandText = hasOrder ? "SELECT relativeorder, word, data FROM dictionary" : "SELECT word, data FROM dictionary";
    var all = new List<(long Order, string Word, string? Data)>();
    using (var r0 = cmd.ExecuteReader())
    {
        while (r0.Read())
        {
            var word = hasOrder ? r0.GetString(1) : r0.GetString(0);
            if (!matchAll && !keep.Contains(word)) continue;
            all.Add((hasOrder ? r0.GetInt64(0) : 0, word, hasOrder ? (r0.IsDBNull(2) ? null : r0.GetString(2)) : (r0.IsDBNull(1) ? null : r0.GetString(1))));
        }
    }

    // Longer headwords are more distinctive for a general English dictionary linked by word
    // overlap; capping keeps a comprehensive dictionary like Webster's from dominating the sample.
    var rows = maxEntries is int max && all.Count > max
        ? all.OrderByDescending(x => x.Word.Length).ThenBy(x => x.Word, StringComparer.OrdinalIgnoreCase).Take(max).ToList()
        : all;

    var count = 0;
    foreach (var (order, word, data) in rows)
    {
        using var ins = dst.CreateCommand();
        var truncated = data is null ? (object)DBNull.Value : Truncate(data, MaxDictionaryChars);
        if (hasOrder)
        {
            ins.CommandText = "INSERT INTO dictionary (relativeorder, word, data) VALUES ($o, $w, $d)";
            ins.Parameters.AddWithValue("$o", order);
            ins.Parameters.AddWithValue("$w", word);
            ins.Parameters.AddWithValue("$d", truncated);
        }
        else
        {
            ins.CommandText = "INSERT INTO dictionary (word, data) VALUES ($w, $d)";
            ins.Parameters.AddWithValue("$w", word);
            ins.Parameters.AddWithValue("$d", truncated);
        }
        ins.ExecuteNonQuery();
        count++;
    }
    Console.WriteLine($"{Path.GetFileNameWithoutExtension(srcPath)}: {count} entrie(s)");
}

// ── Cross-references ─────────────────────────────────────────────────────────────────────────

void ExtractCrossReferences(string scratchDir, string dstPath)
{
    File.Delete(dstPath);
    using var dst = OpenReadWrite(dstPath);
    Exec(dst, """
        CREATE TABLE cross_references (
            id INTEGER PRIMARY KEY AUTOINCREMENT, from_book TEXT, from_chapter INTEGER, from_verse INTEGER,
            to_book TEXT, to_chapter INTEGER, to_verse_start INTEGER, to_verse_end INTEGER, votes INTEGER)
        """);

    var count = 0;
    for (var n = 0; n <= 6; n++)
    {
        var shard = Path.Combine(scratchDir, "cross_references", $"cross_references_{n}.db");
        if (!File.Exists(shard)) continue;

        using var src = OpenReadOnly(shard);
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT from_book, from_chapter, from_verse, to_book, to_chapter, to_verse_start, to_verse_end, votes FROM cross_references";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var fromBook = r.GetString(0);
            var fromChapter = r.GetInt32(1);
            var toBook = r.GetString(3);
            var toChapter = r.GetInt32(4);
            var fromMatch = bookNames.Contains(fromBook) && chaptersByBook[fromBook].Contains(fromChapter);
            var toMatch = bookNames.Contains(toBook) && chaptersByBook[toBook].Contains(toChapter);
            if (!fromMatch && !toMatch) continue;
            if (!r.IsDBNull(7) && r.GetInt32(7) < MinCrossReferenceVotes) continue;

            using var ins = dst.CreateCommand();
            ins.CommandText = """
                INSERT INTO cross_references (id, from_book, from_chapter, from_verse, to_book, to_chapter, to_verse_start, to_verse_end, votes)
                VALUES ($id, $fb, $fc, $fv, $tb, $tc, $tvs, $tve, $vo)
                """;
            ins.Parameters.AddWithValue("$id", ++count);
            ins.Parameters.AddWithValue("$fb", fromBook);
            ins.Parameters.AddWithValue("$fc", fromChapter);
            ins.Parameters.AddWithValue("$fv", r.GetInt32(2));
            ins.Parameters.AddWithValue("$tb", toBook);
            ins.Parameters.AddWithValue("$tc", toChapter);
            ins.Parameters.AddWithValue("$tvs", r.GetInt32(5));
            ins.Parameters.AddWithValue("$tve", r.IsDBNull(6) ? DBNull.Value : r.GetInt32(6));
            ins.Parameters.AddWithValue("$vo", r.IsDBNull(7) ? DBNull.Value : r.GetInt32(7));
            ins.ExecuteNonQuery();
        }
    }
    Console.WriteLine($"cross_references_0: {count} row(s)");
}

// ── Helpers ──────────────────────────────────────────────────────────────────────────────────

static SqliteConnection OpenReadOnly(string path)
{
    var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
    c.Open();
    return c;
}

static SqliteConnection OpenReadWrite(string path)
{
    var c = new SqliteConnection($"Data Source={path}");
    c.Open();
    return c;
}

static void Exec(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static string? ScalarString(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar() as string;
}

static bool TableHasColumn(SqliteConnection conn, string table, string column)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
    return (long)cmd.ExecuteScalar()! > 0;
}

/// <summary>Copies every row of a source query into an insert with positional $0, $1… parameters.</summary>
static void CopyRows(SqliteConnection src, SqliteConnection dst, string selectSql, string insertSql)
{
    using var sel = src.CreateCommand();
    sel.CommandText = selectSql;
    using var r = sel.ExecuteReader();
    while (r.Read())
    {
        using var ins = dst.CreateCommand();
        ins.CommandText = insertSql;
        for (var i = 0; i < r.FieldCount; i++)
            ins.Parameters.AddWithValue($"${i}", r.IsDBNull(i) ? DBNull.Value : r.GetValue(i));
        ins.ExecuteNonQuery();
    }
}

/// <summary>Recreates one table verbatim (schema + every row) — used for small metadata tables.</summary>
static void CopyTableSchemaAndAllRows(SqliteConnection src, SqliteConnection dst, string table)
{
    using (var schemaCmd = src.CreateCommand())
    {
        schemaCmd.CommandText = $"SELECT sql FROM sqlite_master WHERE type='table' AND name='{table}'";
        var sql = (string)schemaCmd.ExecuteScalar()!;
        Exec(dst, sql);
    }

    using var sel = src.CreateCommand();
    sel.CommandText = $"SELECT * FROM \"{table}\"";
    using var r = sel.ExecuteReader();
    while (r.Read())
    {
        var cols = Enumerable.Range(0, r.FieldCount).Select(i => r.GetName(i)).ToList();
        using var ins = dst.CreateCommand();
        ins.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", cols.Select(c => $"\"{c}\""))}) " +
                           $"VALUES ({string.Join(",", cols.Select((_, i) => $"${i}"))})";
        for (var i = 0; i < r.FieldCount; i++)
            ins.Parameters.AddWithValue($"${i}", r.IsDBNull(i) ? DBNull.Value : r.GetValue(i));
        ins.ExecuteNonQuery();
    }
}

static string StripHtml(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");

/// <summary>Lower-case words of at least 4 letters, for linking a chapter to its dictionary entries.</summary>
static IEnumerable<string> ContentWords(string text) =>
    System.Text.RegularExpressions.Regex.Matches(text, $@"[\p{{L}}]{{{MinLinkedWordLength},}}")
        .Select(m => m.Value.ToLowerInvariant());

static string Truncate(string text, int maxChars) =>
    text.Length <= maxChars ? text : text[..maxChars] + " …(truncated for the sample library)";

static string? Arg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Berean.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not find the solution root from " + AppContext.BaseDirectory);
}
