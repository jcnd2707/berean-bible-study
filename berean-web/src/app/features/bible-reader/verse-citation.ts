export interface VerseCitationInput {
  html: string;
  bookName: string;
  chapter: number;
  verse: number;
  translation: string;
}

const NAMED_ENTITIES: Record<string, string> = {
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
  nbsp: " ",
};

function decodeEntities(text: string): string {
  return text.replace(/&(#\d+|#x[0-9a-fA-F]+|[a-zA-Z]+);/g, (match, code: string) => {
    if (code[0] === "#") {
      const codePoint =
        code[1] === "x" || code[1] === "X" ? parseInt(code.slice(2), 16) : parseInt(code.slice(1), 10);
      return Number.isNaN(codePoint) ? match : String.fromCodePoint(codePoint);
    }
    return NAMED_ENTITIES[code] ?? match;
  });
}

/**
 * "For God so loved the world…" — John 3:16 (KJV). Verse text from the API is HTML (Strong's
 * modules add extra markup); tags are stripped and entities decoded here rather than relying on
 * a live DOM, so this stays plain and unit-testable.
 */
export function formatVerseCitation(input: VerseCitationInput): string {
  const plain = decodeEntities(input.html.replace(/<[^>]*>/g, ""))
    .replace(/\s+/g, " ")
    .trim();
  return `"${plain}" — ${input.bookName} ${input.chapter}:${input.verse} (${input.translation})`;
}
