// ── Resource catalogue ────────────────────────────────────────────────────────

export interface BibleModule {
  moduleId: string;
  name: string; // full name, e.g. "American Standard Version (1901)"
  language: string;
  filePath?: string;
}

export interface CommentaryModule {
  moduleId: string;
  name: string;
  language: string;
  filePath?: string;
}

export interface DictionaryModule {
  moduleId: string;
  name: string;
  language: string;
  filePath?: string;
}

// ── Bible text ────────────────────────────────────────────────────────────────

export interface BookEntry {
  number: number; // 1-39 = OT, 40-66 = NT
  name: string; // e.g. "Genesis"
  abbreviation: string; // e.g. "Gen" — used as the book param in API calls
  chapterCount: number;
}

export interface Verse {
  book: number; // book number (1-66)
  bookName: string; // e.g. "Genesis"
  chapter: number;
  verse: number;
  reference: string; // e.g. "Genesis 1:1"
  text: string;
}

export interface ChapterResponse {
  moduleId?: string;
  book: number;
  bookName: string;
  chapter: number;
  verses: Verse[];
}

export interface VerseResponse extends Verse {
  moduleId?: string;
}

// ── Commentary ────────────────────────────────────────────────────────────────

export interface CommentaryEntry {
  book: string;
  chapter: number;
  verse?: number;
  heading?: string;
  html: string; // commentary is rich-text HTML from e-Sword
}

export interface CommentaryResponse {
  moduleId: string;
  book: string;
  chapter: number;
  entries: CommentaryEntry[];
}

// ── Cross-references ──────────────────────────────────────────────────────────

export interface CrossReference {
  fromBook: string;
  fromChapter: number;
  fromVerse: number;
  toBook: string;
  toChapter: number;
  toVerse: number;
  votes: number;
}

export interface CrossReferencesResponse {
  references: CrossReference[];
}

// ── Dictionary ────────────────────────────────────────────────────────────────

export interface DictionaryEntry {
  word: string;
  strongs?: string;
  transliteration?: string;
  definition: string; // may be HTML
  partOfSpeech?: string;
}

export interface DictionarySearchResult {
  word: string;
  strongs?: string;
  snippet: string;
}

// ── Notes ─────────────────────────────────────────────────────────────────────

export interface Note {
  reference: string; // e.g. "Gen.1.1"
  text: string;
  updatedAt?: string;
}

export interface UpsertNoteRequest {
  text: string;
}

// ── Navigation state (shared signal contract) ─────────────────────────────────

export interface BibleLocation {
  moduleId: string;
  book: string;
  chapter: number;
  verse: number | null;
}
