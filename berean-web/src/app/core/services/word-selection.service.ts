import { Injectable, signal } from "@angular/core";

export interface WordSelection {
  word: string; // cleaned plain word
  strongs: string | null; // e.g. "H430" if extracted from KJV text, else null
}

@Injectable({ providedIn: "root" })
export class WordSelectionService {
  private readonly _selection = signal<WordSelection | null>(null);
  readonly selection = this._selection.asReadonly();

  select(word: string, strongs: string | null = null): void {
    this._selection.set({ word, strongs });
  }

  clear(): void {
    this._selection.set(null);
  }
}
