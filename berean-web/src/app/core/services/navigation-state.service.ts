import { Injectable, signal, computed } from "@angular/core";
import { BibleLocation, Verse, CommentaryEntry, BookEntry } from "../models";

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
  }
  closeNotesList(): void {
    this._showNotesList.set(false);
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
