// ── Resource catalogue ────────────────────────────────────────────────────────

export interface BibleModule {
  moduleId: string;
  name: string;
  language: string;
  filePath?: string;
}

export interface BibleModuleDetails {
  translation: string;
  title: string;
  license: string | null;
  hasStrongs: boolean;
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
  number: number;
  name: string;
  abbreviation: string;
  chapterCount: number;
}

export interface StrongsWord {
  word: string;
  number: string; // e.g. "H430"
}

export interface Verse {
  book: number;
  bookName: string;
  chapter: number;
  verse: number;
  reference: string;
  text: string;
  strongsWords?: StrongsWord[];
}

export interface VerseResponse extends Verse {
  moduleId?: string;
  strongsWords?: StrongsWord[];
}

export interface ChapterResponse {
  moduleId?: string;
  book: number;
  bookName: string;
  chapter: number;
  verses: Verse[];
}

// ── Commentary ────────────────────────────────────────────────────────────────

export interface CommentaryEntry {
  book: number;
  bookName: string;
  chapter: number;
  verseBegin: number;
  verseEnd: number;
  reference: string;
  marker: string | null;
  text: string;
}

export interface CommentaryResponse {
  moduleId: string;
  book: number;
  bookName: string;
  chapter: number;
  entries: CommentaryEntry[];
}

// ── Cross-references ──────────────────────────────────────────────────────────

export interface CrossReference {
  fromReference: string;
  toReference: string;
  toBook: number;
  toChapter: number;
  toVerseStart: number;
  toVerseEnd: number;
  votes: number;
}

export interface CrossReferencesResponse {
  reference: string;
  references: CrossReference[];
}

// ── Dictionary ────────────────────────────────────────────────────────────────

export interface DictionaryEntry {
  word: string;
  strongs?: string;
  transliteration?: string;
  definition: string;
  partOfSpeech?: string;
}

export interface DictionarySearchResult {
  word: string;
  strongs?: string;
  snippet: string;
}

// ── Notes ─────────────────────────────────────────────────────────────────────

export interface Note {
  reference: string;
  text: string;
  updatedAt?: string;
}

export interface ReadChapter {
  book: number; // canonical 1-66, not a module's abbreviation
  chapter: number;
  readAt: string;
}

export interface UpsertNoteRequest {
  text: string;
}

// ── Navigation state ──────────────────────────────────────────────────────────

export interface BibleLocation {
  moduleId: string;
  book: string;
  chapter: number;
  verse: number | null;
}

// ── Search ────────────────────────────────────────────────────────────────────

export interface SearchResult {
  book: number;
  bookName: string;
  chapter: number;
  verse: number;
  reference: string;
  text: string;
}

export type Testament = "OT" | "NT" | "both";

export interface SearchParams {
  q: string;
  limit?: number;
  testament?: Testament;
  book?: string; // book abbreviation e.g. "Rom"
}

// ── Books ─────────────────────────────────────────────────────────────────────

export interface BookSummary {
  moduleId: string;
  title: string;
  author?: string;
  publisher?: string;
  language?: string;
}

export interface BookMeta extends BookSummary {
  isbn?: string;
  rights?: string;
  importedAt?: string;
}

export interface BookChapter {
  id: number;
  chapterNumber: number;
  title: string;
  orderIndex: number;
}

export interface BookParagraph {
  id: number;
  orderedIndex: number;
  cssClass: string;
  content: string; // inner HTML, safe to render
}

export interface BookChapterContent {
  chapter: BookChapter;
  paragraphs: BookParagraph[];
}
