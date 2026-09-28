export interface ParsedDefinition {
  topic: string;
  original?: string;
  transliteration?: string;
  phonetic?: string;
  bdbDefinitions: string[];
  origin?: string;
  partOfSpeech?: string;
  strongsDefinition?: string;
  isStrongs: boolean;
  // For plain-word modules (Easton's etc.)
  plainText?: string;
}

/**
 * Parses a Strong's dictionary definition string into structured fields.
 * Falls back to plain text if it doesn't look like a Strong's entry.
 */
export function parseDefinition(topic: string, raw: string): ParsedDefinition {
  const isStrongs = /^[HG]\d+/.test(topic);

  if (!isStrongs) {
    return { topic, isStrongs: false, bdbDefinitions: [], plainText: raw };
  }

  const result: ParsedDefinition = {
    topic,
    isStrongs: true,
    bdbDefinitions: [],
  };

  // Original word
  const origMatch = raw.match(/Original:\s*([^\n]+?)(?=\s+Transliteration:|$)/);
  result.original = origMatch?.[1]?.trim();

  // Transliteration
  const transMatch = raw.match(
    /Transliteration:\s*([^\n]+?)(?=\s+Phonetic:|$)/,
  );
  result.transliteration = transMatch?.[1]?.trim();

  // Phonetic — bounded by whichever "definition" section label follows: "BDB Definition" for
  // Hebrew entries, "Thayer Definition" for Greek ones. Missing the Greek case here used to let
  // this capture the rest of the whole entry (definitions, origin, KJV occurrences and all).
  const phonMatch = raw.match(
    /Phonetic:\s*([^\n]+?)(?=\s+(?:BDB Definition|Thayer Definition|Origin:|TDNT entry:|TWOT entry:|Part\(s\) of speech|Strong's Definition)|$)/,
  );
  result.phonetic = phonMatch?.[1]?.trim();

  // BDB Definition — the list of meanings between "BDB Definition :" and "Origin:"
  const bdbMatch = raw.match(
    /BDB Definition\s*:\s*([\s\S]+?)(?=Origin:|TWOT entry:|Part\(s\)|Strong's Definition|$)/i,
  );
  if (bdbMatch) {
    result.bdbDefinitions = bdbMatch[1]
      .split(/\n|\r/)
      .map((l) => l.trim())
      .filter((l) => l.length > 0 && !l.startsWith("("));
  }

  // Origin
  const originMatch = raw.match(
    /Origin:\s*([^\n]+?)(?=\s+TWOT|\s+Part\(s\)|$)/,
  );
  result.origin = originMatch?.[1]?.trim();

  // Part of speech
  const posMatch = raw.match(
    /Part\(s\) of speech:\s*([^\n]+?)(?=\s+Strong|$)/i,
  );
  result.partOfSpeech = posMatch?.[1]?.trim();

  // Strong's Definition — up to "Total KJV"
  const sdefMatch = raw.match(
    /Strong's Definition\s*:\s*([\s\S]+?)(?=Total KJV|$)/i,
  );
  if (sdefMatch) {
    result.strongsDefinition = sdefMatch[1].trim();
  }

  return result;
}
