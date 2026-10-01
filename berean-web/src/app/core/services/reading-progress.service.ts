import { Injectable, inject, effect, untracked } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { firstValueFrom } from "rxjs";
import { environment } from "../../../environments/environment";
import { NavigationStateService } from "./navigation-state.service";
import { ProfileService } from "./profile.service";
import { ResourcesService } from "./resources.service";
import { BibleService } from "./bible.service";
import { ReadChapter } from "../models";

/**
 * Which Bible chapters the current profile has marked read. The data lives on the server, per
 * profile, so it follows the person to any device; this service loads it once the profile is
 * known and mirrors every change into NavigationStateService, which is what the reader and the
 * book sidebar read from.
 */
@Injectable({ providedIn: "root" })
export class ReadingProgressService {
  private readonly http = inject(HttpClient);
  private readonly nav = inject(NavigationStateService);
  private readonly profiles = inject(ProfileService);
  private readonly resourcesService = inject(ResourcesService);
  private readonly bibleService = inject(BibleService);
  private readonly base = `${environment.apiBaseUrl}/api/progress/chapters`;

  constructor() {
    // Runs at startup once the stored profile resolves, and again on the picker's first-ever
    // choice. Switching profiles reloads the page, so it starts clean rather than clearing here.
    effect(() => {
      const profile = this.profiles.current();
      if (!profile) return;
      untracked(() => void this.load(profile.id)); // the load reads signals it must not subscribe to
    });
  }

  private async load(profileId: string): Promise<void> {
    await this.ensureBooks();

    try {
      const chapters = await firstValueFrom(this.http.get<ReadChapter[]>(this.base));
      // Ignore a response that arrives after the profile has changed underneath it.
      if (this.profiles.current()?.id === profileId) this.nav.setReadChapters(chapters);
    } catch {
      // Leave everything unmarked; marking a chapter will still work and re-sync from the server.
    }
  }

  /**
   * The reader needs a canonical book number to mark a chapter, and the book list is otherwise
   * only loaded when the sidebar mounts — which on phone and tablet is when the drawer opens, so
   * a chat citation can land on a chapter before it has.
   */
  private async ensureBooks(): Promise<void> {
    if (this.nav.books().length) return;
    try {
      const modules = await firstValueFrom(this.resourcesService.getBibles());
      if (!modules.length) return;
      const books = await firstValueFrom(this.bibleService.getBooks(modules[0].moduleId));
      if (!this.nav.books().length) this.nav.setBooks(books);
    } catch {
      /* the sidebar will load them when it mounts */
    }
  }

  /**
   * Marks or unmarks a chapter. The change shows at once and is rolled back if the server
   * refuses it. Resolves true when it stuck.
   */
  async setRead(book: number, chapter: number, read: boolean): Promise<boolean> {
    const previous = this.nav.readAtForChapter(book, chapter);
    this.nav.setChapterRead(book, chapter, read ? (previous ?? new Date().toISOString()) : null);

    try {
      if (read) {
        const saved = await firstValueFrom(this.http.put<ReadChapter>(`${this.base}/${book}/${chapter}`, null));
        this.nav.setChapterRead(book, chapter, saved.readAt);
      } else {
        await firstValueFrom(this.http.delete<void>(`${this.base}/${book}/${chapter}`));
      }
      return true;
    } catch (err) {
      // Unmarking something the server never had is the end state we wanted anyway.
      if (!read && (err as { status?: number }).status === 404) return true;
      this.nav.setChapterRead(book, chapter, previous);
      return false;
    }
  }
}
