export interface PhoneticSpeech {
  syllables: string[];
  stressIndex: number | null;
  spokenNormal: string;
  spokenSlow: string;
}

export const PHONETIC_NORMAL_RATE = 0.9;
export const PHONETIC_SLOW_RATE = 0.6;

/**
 * Turns a Strong's pronunciation spelling ("ag-ah'-pay") into syllables plus two spoken forms.
 * Joining with spaces vs. commas is only a starting point — Web Speech has no reliable stress or
 * SSML support, so the exact wording is tuned by ear against a fixed word list (see the plan's
 * Phase 1 listening check). This function only owns the parsing.
 */
export function phoneticToSpeech(phonetic: string): PhoneticSpeech | null {
  if (!phonetic) return null;

  // Strip anything but letters, hyphens, apostrophes (straight or curly) and spaces, so stray
  // punctuation in an odd entry can't make the voice read it aloud.
  const cleaned = phonetic
    .replace(/’/g, "'")
    .replace(/[^A-Za-z'\-\s]/g, "")
    .trim();
  if (!cleaned) return null;

  const rawSyllables = cleaned
    .split("-")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
  if (rawSyllables.length === 0) return null;

  let stressIndex: number | null = null;
  const syllables = rawSyllables.map((syl, i) => {
    if (syl.includes("'")) {
      if (stressIndex === null) stressIndex = i;
      return syl.replace(/'/g, "");
    }
    return syl;
  });

  return {
    syllables,
    stressIndex,
    spokenNormal: syllables.join(" "),
    spokenSlow: syllables.join(", "),
  };
}
