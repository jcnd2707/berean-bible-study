/**
 * Trims surrounding punctuation from a word picked up in the reader, for speech. Keeps accents
 * and case — the dictionary lookup has its own, separate ASCII/lowercase cleaning, which this
 * doesn't touch.
 */
export function wordForSpeech(raw: string): string {
  return raw.replace(/^[^\p{L}]+|[^\p{L}]+$/gu, "");
}

/** Lowercased, with a trailing possessive "'s" removed, for looking a word up in the overrides file. */
export function overrideKey(word: string): string {
  return word.toLowerCase().replace(/['’]s$/, "");
}
