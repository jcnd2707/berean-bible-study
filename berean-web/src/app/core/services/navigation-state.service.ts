import { Injectable, signal, computed } from '@angular/core';
import { BibleLocation } from '../models';

/**
 * Single source of truth for the active Bible location.
 * All panels (reader, commentary, dictionary, chat) read from here.
 * The context strip for the Agent API is derived from this signal.
 */
@Injectable({ providedIn: 'root' })
export class NavigationStateService {
  // Active location — null until the user has picked a module + book
  private readonly _location = signal<BibleLocation | null>(null);

  readonly location = this._location.asReadonly();

  // Convenience computed selectors
  readonly moduleId = computed(() => this._location()?.moduleId ?? null);
  readonly book     = computed(() => this._location()?.book ?? null);
  readonly chapter  = computed(() => this._location()?.chapter ?? null);
  readonly verse    = computed(() => this._location()?.verse ?? null);

  /** Context strip payload for the Agent API */
  readonly contextStrip = computed(() => {
    const loc = this._location();
    if (!loc) return null;
    return {
      moduleId: loc.moduleId,
      book:     loc.book,
      chapter:  loc.chapter,
      verse:    loc.verse,
    };
  });

  navigate(location: BibleLocation): void {
    this._location.set(location);
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
}
