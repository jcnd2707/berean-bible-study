import { Injectable, inject, effect } from "@angular/core";
import { firstValueFrom } from "rxjs";
import { NavigationStateService } from "./navigation-state.service";
import { ProfileService } from "./profile.service";
import { ResourcesService } from "./resources.service";
import { BibleService } from "./bible.service";
import { readingPositionStorageKey } from "./conversation-storage-key";
import { resolveRestoredPosition } from "./reading-position";
import { BibleLocation } from "../models";

/**
 * Remembers where you were reading, per profile, in this browser (§D8). Restoring is per device
 * for now — syncing across devices belongs with bookmarks in PR 3, which adds per-profile server
 * storage anyway.
 */
@Injectable({ providedIn: "root" })
export class ReadingPositionService {
  private readonly nav = inject(NavigationStateService);
  private readonly profiles = inject(ProfileService);
  private readonly resourcesService = inject(ResourcesService);
  private readonly bibleService = inject(BibleService);

  constructor() {
    this.restore();

    // Opening a session from the list navigates explicitly and is meant to win; the automatic
    // chat resume on load never calls navigate(), so the two never fight over this effect.
    effect(() => {
      const loc = this.nav.location();
      if (loc) this.save(loc);
    });
  }

  private save(loc: BibleLocation): void {
    try {
      const key = readingPositionStorageKey(this.profiles.current()?.id ?? null);
      localStorage.setItem(key, JSON.stringify(loc));
    } catch {
      /* private browsing, storage disabled, etc. */
    }
  }

  private async restore(): Promise<void> {
    await this.profiles.ready;
    if (this.nav.location()) return;

    let saved: string | null;
    try {
      saved = localStorage.getItem(readingPositionStorageKey(this.profiles.current()?.id ?? null));
    } catch {
      return;
    }
    if (!saved) return;

    let modules;
    try {
      modules = await firstValueFrom(this.resourcesService.getBibles());
    } catch {
      return;
    }
    if (!modules.length) return;

    let books;
    try {
      books = await firstValueFrom(this.bibleService.getBooks(modules[0].moduleId));
    } catch {
      return;
    }

    const resolved = resolveRestoredPosition(saved, modules, books);
    if (!resolved || this.nav.location()) return;

    this.nav.setBooks(books);
    const book = books.find((b) => b.abbreviation === resolved.book);
    if (book) this.nav.setMaxChapter(book.chapterCount);
    this.nav.navigate(resolved);
  }
}
