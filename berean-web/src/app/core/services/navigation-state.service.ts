import { Injectable, inject, signal, computed } from "@angular/core";
import { BibleLocation, Verse, CommentaryEntry, BookEntry } from "../models";
import { BackStackService } from "./back-stack.service";

export type RightTab = "commentary" | "notes" | "xrefs" | "dictionary" | "ask";

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
  private readonly backStack = inject(BackStackService);

  // Stable closer references so BackStackService can find/replace the right
  // one — a fresh arrow function on every call wouldn't be recognizable.
  private readonly closeSearchFn = () => this.closeSearch();
  private readonly closeCompareFn = () => this.closeCompare();
  private readonly closeNotesListFn = () => this.closeNotesList();
  private readonly closeBooksFn = () => this.closeBooks();
  private readonly closeBookDrawerFn = () => this.closeBookDrawer();
  private readonly closeProfileSwitcherFn = () => this.closeProfileSwitcher();

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
  // Tablet/phone book+chapter navigation drawer (desktop shows BookSidebar inline instead).
  private readonly _showBookDrawer = signal<boolean>(false);
  private readonly _showProfileSwitcher = signal<boolean>(false);
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
  readonly showBookDrawer = this._showBookDrawer.asReadonly();
  readonly showProfileSwitcher = this._showProfileSwitcher.asReadonly();

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
    if (this._showSearch()) {
      this.closeSearch();
      return;
    }
    this._showCompare.set(false); // mutual exclusion only — doesn't touch the back stack, open() below replaces it in place
    this._showSearch.set(true);
    this.backStack.open(this.closeSearchFn);
  }
  closeSearch(): void {
    if (!this._showSearch()) return;
    this._showSearch.set(false);
    this.backStack.close(this.closeSearchFn);
  }
  toggleCompare(): void {
    if (this._showCompare()) {
      this.closeCompare();
      return;
    }
    this._showSearch.set(false);
    this._showCompare.set(true);
    this.backStack.open(this.closeCompareFn);
  }
  closeCompare(): void {
    if (!this._showCompare()) return;
    this._showCompare.set(false);
    this.backStack.close(this.closeCompareFn);
  }
  toggleNotesList(): void {
    if (this._showNotesList()) {
      this.closeNotesList();
      return;
    }
    this._showBooks.set(false);
    this._showNotesList.set(true);
    this.backStack.open(this.closeNotesListFn);
  }
  closeNotesList(): void {
    if (!this._showNotesList()) return;
    this._showNotesList.set(false);
    this.backStack.close(this.closeNotesListFn);
  }
  toggleBooks(): void {
    if (this._showBooks()) {
      this.closeBooks();
      return;
    }
    this._showNotesList.set(false);
    this._showSearch.set(false);
    this._showCompare.set(false);
    this._showBooks.set(true);
    this.backStack.open(this.closeBooksFn);
  }
  closeBooks(): void {
    if (!this._showBooks()) return;
    this._showBooks.set(false);
    this.backStack.close(this.closeBooksFn);
  }

  toggleBookDrawer(): void {
    if (this._showBookDrawer()) {
      this.closeBookDrawer();
      return;
    }
    this._showBookDrawer.set(true);
    this.backStack.open(this.closeBookDrawerFn);
  }
  closeBookDrawer(): void {
    if (!this._showBookDrawer()) return;
    this._showBookDrawer.set(false);
    this.backStack.close(this.closeBookDrawerFn);
  }

  toggleProfileSwitcher(): void {
    if (this._showProfileSwitcher()) {
      this.closeProfileSwitcher();
      return;
    }
    this._showProfileSwitcher.set(true);
    this.backStack.open(this.closeProfileSwitcherFn);
  }
  closeProfileSwitcher(): void {
    if (!this._showProfileSwitcher()) return;
    this._showProfileSwitcher.set(false);
    this.backStack.close(this.closeProfileSwitcherFn);
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
    this.closeBooks();
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
    this.backStack.open(this.closeBooksFn);
    this._requestedBook.set({ moduleId, chapterIndex });
  }

  /** One-tap jump from the verse action bar (MOBILE_PLAN.md §3) to a study-panel tab. */
  requestRightTab(tab: RightTab): void {
    this._requestedRightTab.set(tab);
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
