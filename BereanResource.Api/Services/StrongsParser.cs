using System.Text.RegularExpressions;
using BereanResourceApi.Models;

namespace BereanResourceApi.Services;

/// <summary>
/// Parses inline Strong's tags from Bible verse text.
///
/// Scrollmapper format (wrapping):
///   <WH7225>In the beginning</WH7225> <WH430>God</WH430>
///
/// MySword format (trailing, no closing tag, consecutive tags allowed):
///   In the beginning<WH7225> God<WH430> created<WH1254><WH853>
///
/// Both formats use H = Hebrew (OT) and G = Greek (NT).
/// Returns (plainText, null) when no tags are present.
/// </summary>
public static class StrongsParser
{
    // ── Scrollmapper ──────────────────────────────────────────────────────────

    private static readonly Regex ScrollmapperTag = new(
        @"<W([HG])(\d+)>(.*?)</W[HG]\d+>",
        RegexOptions.Compiled | RegexOptions.Singleline
    );

    private static readonly Regex ResidualTags = new(
        @"<[^>]+>",
        RegexOptions.Compiled
    );

    // ── MySword ───────────────────────────────────────────────────────────────

    // Captures a word chunk followed by one or more consecutive trailing tags.
    // Group 1 = word text, Group 2 = all consecutive tags e.g. <WH1254><WH853>
    private static readonly Regex MySwordChunk = new(
        @"([^<]+?)\s*(<(?:WH|WG)\d+>(?:<(?:WH|WG)\d+>)*)",
        RegexOptions.Compiled
    );

    // Extracts individual numbers from a tag block.
    private static readonly Regex MySwordNumber = new(
        @"<W([HG])(\d+)>",
        RegexOptions.Compiled
    );

    // ── Public API ────────────────────────────────────────────────────────────

    public static (string PlainText, List<StrongsWord>? Words) Parse(
        string rawText, BibleFormat format) => format switch
        {
            BibleFormat.MySword => ParseMySword(rawText),
            BibleFormat.Scrollmapper => ParseScrollmapper(rawText),
            _ => (rawText.Trim(), null)
        };

    // ── Scrollmapper parser ───────────────────────────────────────────────────

    private static (string, List<StrongsWord>?) ParseScrollmapper(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (string.Empty, null);

        if (!raw.Contains("<W"))
            return (raw.Trim(), null);

        var words = new List<StrongsWord>();

        var plain = ScrollmapperTag.Replace(raw, m =>
        {
            var prefix = m.Groups[1].Value;
            var digits = m.Groups[2].Value;
            var word = m.Groups[3].Value.Trim();
            words.Add(new StrongsWord(Word: word, Number: $"{prefix}{digits}"));
            return word;
        });

        plain = ResidualTags.Replace(plain, string.Empty);
        plain = string.Join(' ', plain.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return (plain.Trim(), words.Count > 0 ? words : null);
    }

    // ── MySword parser ────────────────────────────────────────────────────────

    private static (string, List<StrongsWord>?) ParseMySword(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (string.Empty, null);

        if (!raw.Contains("<W"))
            return (raw.Trim(), null);

        var words = new List<StrongsWord>();
        var lastEnd = 0;
        var plainBuf = new System.Text.StringBuilder();

        foreach (Match m in MySwordChunk.Matches(raw))
        {
            // Capture any unmatched text before this match (e.g. leading words
            // that have no Strong's tag — rare but possible in partial tagging).
            if (m.Index > lastEnd)
                plainBuf.Append(raw[lastEnd..m.Index]);

            var word = m.Groups[1].Value.Trim();
            plainBuf.Append(raw[m.Index..(m.Index + m.Groups[1].Length)]);

            // A single word may map to multiple consecutive tags.
            foreach (Match num in MySwordNumber.Matches(m.Groups[2].Value))
                words.Add(new StrongsWord(
                    Word: word,
                    Number: $"{num.Groups[1].Value}{num.Groups[2].Value}"
                ));

            lastEnd = m.Index + m.Length;
        }

        // Trailing text after the last tag (e.g. final punctuation).
        if (lastEnd < raw.Length)
            plainBuf.Append(raw[lastEnd..]);

        // Strip any residual tags and normalise whitespace.
        var plain = ResidualTags.Replace(plainBuf.ToString(), string.Empty).Trim();

        return (plain, words.Count > 0 ? words : null);
    }
}