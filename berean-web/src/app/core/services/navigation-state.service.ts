import { Injectable, signal, computed } from "@angular/core";
import { BibleLocation } from "../models";

/**
 * Single source of truth for the active Bible location.
 * All panels (reader, commentary, dictionary, chat) read from here.
 * The context strip for the Agent API is derived from this signal.
 */
@Injectable({ providedIn: "root" })
export class NavigationStateService {
  private readonly _location = signal<BibleLocation | null>(null);
  private readonly _maxChapter = signal<number>(1);

  readonly location = this._location.asReadonly();
  readonly maxChapter = this._maxChapter.asReadonly();

  readonly moduleId = computed(() => this._location()?.moduleId ?? null);
  readonly book = computed(() => this._location()?.book ?? null);
  readonly chapter = computed(() => this._location()?.chapter ?? null);
  readonly verse = computed(() => this._location()?.verse ?? null);

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

  navigate(location: BibleLocation): void {
    this._location.set(location);
  }

  setMaxChapter(max: number): void {
    this._maxChapter.set(max);
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
