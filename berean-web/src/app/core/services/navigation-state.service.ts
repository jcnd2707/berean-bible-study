import { Injectable, signal, computed } from "@angular/core";
import { BibleLocation, Verse, CommentaryEntry, BookEntry } from "../models";

export type RightTab = "commentary" | "notes" | "xrefs";

/** Asks the book reader to open a book at a chapter (1-based position in the book's chapter list). */
export interface BookRequest {
  moduleId: string;
  chapterIndex: number;
}

export interface WordContext {
  word: string;
  strongs: string | null;
  definition: string; // formatted definition text
  source: string; // e.g. "Strong's H430" or "Easton's"
}

@Injectable({ providedIn: "root" })
export class NavigationStateService {
  private readonly _location = signal<BibleLocation | null>(null);
  private readonly _maxChapter = signal<number>(1);
  private readonly _hasStrongs = signal<boolean>(false);
  private readonly _verses = signal<Verse[]>([]);
  private readonly _books = signal<BookEntry[]>([]);
  private readonly _activeCommentary = signal<CommentaryEntry | null>(null);
  private readonly _activeCommentaryMod = signal<string>("");
  private readonly _activeWord = signal<WordContext | null>(null);
  private readonly _showSearch = signal<boolean>(false);
  private readonly _showCompare = signal<boolean>(false);
  private readonly _notedReferences = signal<Set<string>>(new Set());
  private readonly _showNotesList = signal<boolean>(false);
  private readonly _showBooks = signal<boolean>(false);
  private readonly _requestedRightTab = signal<RightTab | null>(null);
  private readonly _requestedCommentaryModule = signal<string | null>(null);
  private readonly _requestedBook = signal<BookRequest | null>(null);

  readonly location = this._location.asReadonly();
  readonly maxChapter = this._maxChapter.asReadonly();
  readonly hasStrongs = this._hasStrongs.asReadonly();
  readonly verses = this._verses.asReadonly();
  readonly books = this._books.asReadonly();
  readonly activeCommentary = this._activeCommentary.asReadonly();
  readonly activeCommentaryMod = this._activeCommentaryMod.asReadonly();
  readonly activeWord = this._activeWord.asReadonly();
  readonly showSearch = this._showSearch.asReadonly();
  readonly showCompare = this._showCompare.asReadonly();
  readonly notedReferences = this._notedReferences.asReadonly();
  readonly showNotesList = this._showNotesList.asReadonly();
  readonly showBooks = this._showBooks.asReadonly();

  // One-shot requests the panels consume (used when a chat citation is clicked).
  readonly requestedRightTab = this._requestedRightTab.asReadonly();
  readonly requestedCommentaryModule = this._requestedCommentaryModule.asReadonly();
  readonly requestedBook = this._requestedBook.asReadonly();

  readonly moduleId = computed(() => this._location()?.moduleId ?? null);
  readonly book = computed(() => this._location()?.book ?? null);
  readonly chapter = computed(() => this._location()?.chapter ?? null);
  readonly verse = computed(() => this._location()?.verse ?? null);

  readonly activeVerseText = computed(() => {
    const v = this._location()?.verse;
    if (!v) return null;
    return this._verses().find((vs) => vs.verse === v)?.text ?? null;
  });

  readonly contextStrip = computed(() => {
    const loc = this._location();
    if (!loc) return null;
    return {
      moduleId: loc.moduleId,
      book: loc.book,
      chapter: loc.chapter,
      verse: loc.verse,
    };
  });

  toggleSearch(): void {
    this._showSearch.update((v) => !v);
    this._showCompare.set(false);
  }
  closeSearch(): void {
    this._showSearch.set(false);
  }
  toggleCompare(): void {
    this._showCompare.update((v) => !v);
    this._showSearch.set(false);
  }
  closeCompare(): void {
    this._showCompare.set(false);
  }
  toggleNotesList(): void {
    this._showNotesList.update((v) => !v);
    this._showBooks.set(false);
  }
  closeNotesList(): void {
    this._showNotesList.set(false);
  }
  toggleBooks(): void {
    this._showBooks.update((v) => !v);
    this._showNotesList.set(false);
    this._showSearch.set(false);
    this._showCompare.set(false);
  }
  closeBooks(): void {
    this._showBooks.set(false);
  }

  /** Shows a commentary on a passage: go to the verse and open that module in the commentary tab. */
  openCommentary(
    moduleId: string,
    bookNumber: number,
    chapter: number,
    verse: number | null,
  ): void {
    const abbr = this.bookAbbrFromNumber(bookNumber);
    if (!abbr) return;
    const translation = this._location()?.moduleId ?? "KJV";
    this._showBooks.set(false);
    this._location.set({ moduleId: translation, book: abbr, chapter, verse });
    this._requestedCommentaryModule.set(moduleId);
    this._requestedRightTab.set("commentary");
  }

  /** Opens the book reader on a chapter of a prose book. */
  openBookChapter(moduleId: string, chapterIndex: number): void {
    this._showNotesList.set(false);
    this._showSearch.set(false);
    this._showCompare.set(false);
    this._showBooks.set(true);
    this._requestedBook.set({ moduleId, chapterIndex });
  }

  clearRequestedRightTab(): void {
    this._requestedRightTab.set(null);
  }
  clearRequestedCommentaryModule(): void {
    this._requestedCommentaryModule.set(null);
  }
  clearRequestedBook(): void {
    this._requestedBook.set(null);
  }

  setNotedReferences(refs: string[]): void {
    this._notedReferences.set(new Set(refs));
  }

  hasNoteForChapter(book: string, chapter: number): boolean {
    const prefix = `${book}.${chapter}`;
    for (const r of this._notedReferences()) {
      if (r === prefix || r.startsWith(prefix + ".")) return true;
    }
    return false;
  }

  setBooks(books: BookEntry[]): void {
    this._books.set(books);
  }

  bookAbbrFromNumber(bookNumber: number): string | null {
    return (
      this._books().find((b) => b.number === bookNumber)?.abbreviation ?? null
    );
  }

  /** Matches full name, abbreviation, or partial name (case-insensitive) */
  bookAbbrFromName(input: string): string | null {
    const q = input.toLowerCase().trim();
    const books = this._books();
    // Exact abbreviation match first
    let found = books.find((b) => b.abbreviation.toLowerCase() === q);
    if (found) return found.abbreviation;
    // Exact full name
    found = books.find((b) => b.name.toLowerCase() === q);
    if (found) return found.abbreviation;
    // Starts-with match on name
    found = books.find((b) => b.name.toLowerCase().startsWith(q));
    if (found) return found.abbreviation;
    return null;
  }

  navigate(location: BibleLocation): void {
    this._location.set(location);
  }

  setMaxChapter(max: number): void {
    this._maxChapter.set(max);
  }

  setHasStrongs(value: boolean): void {
    this._hasStrongs.set(value);
  }

  setVerses(verses: Verse[]): void {
    this._verses.set(verses);
  }

  /** Called by CommentaryComponent when the active verse matches an entry */
  setActiveCommentary(entry: CommentaryEntry | null, moduleId: string): void {
    this._activeCommentary.set(entry);
    this._activeCommentaryMod.set(moduleId);
  }

  /** Called by DictionaryPanelComponent when a lookup result is shown */
  setActiveWord(ctx: WordContext | null): void {
    this._activeWord.set(ctx);
  }

  selectVerse(verse: number): void {
    const current = this._location();
    if (!current) return;
    this._location.set({ ...current, verse });
  }

  clearVerse(): void {
    const current = this._location();
    if (!current) return;
    this._location.set({ ...current, verse: null });
  }

  prevChapter(): void {
    const loc = this._location();
    if (!loc || loc.chapter <= 1) return;
    this._location.set({ ...loc, chapter: loc.chapter - 1, verse: null });
  }

  nextChapter(): void {
    const loc = this._location();
    if (!loc || loc.chapter >= this._maxChapter()) return;
    this._location.set({ ...loc, chapter: loc.chapter + 1, verse: null });
  }
}
