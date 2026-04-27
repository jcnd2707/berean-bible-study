import {
  Component,
  OnInit,
  inject,
  signal,
  computed,
  HostBinding,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { toObservable } from "@angular/core/rxjs-interop";
import {
  switchMap,
  tap,
  catchError,
  distinctUntilChanged,
} from "rxjs/operators";
import { EMPTY, forkJoin } from "rxjs";

import { BibleService } from "../../core/services/bible.service";
import { ResourcesService } from "../../core/services/resources.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { WordSelectionService } from "../../core/services/word-selection.service";
import {
  BibleModuleDetails,
  ChapterResponse,
  Verse,
  StrongsWord,
} from "../../core/models";
import { PreferencesService } from "../../core/services/preferences.service";
import { SearchPanelComponent } from "../search/search-panel.component";
import { ComparePanelComponent } from "../compare/compare-panel.component";
import { NotesListComponent } from "../notes/notes-list.component";
import { BookReaderComponent } from "../books/book-reader.component";

interface TabModule {
  moduleId: string;
  label: string; // short display label e.g. "BSB", "KJV"
  title: string; // full title for tooltip
  hasStrongs: boolean;
}

@Component({
  selector: "app-bible-reader",
  standalone: true,
  imports: [
    CommonModule,
    SearchPanelComponent,
    ComparePanelComponent,
    NotesListComponent,
    BookReaderComponent,
  ],
  templateUrl: "./bible-reader.component.html",
  styleUrl: "./bible-reader.component.scss",
})
export class BibleReaderComponent implements OnInit {
  private readonly bibleService = inject(BibleService);
  private readonly resourcesService = inject(ResourcesService);
  readonly navState = inject(NavigationStateService);
  private readonly wordSelection = inject(WordSelectionService);
  readonly prefs = inject(PreferencesService);

  @HostBinding("style.--reader-font-size")
  get hostFontSize(): string {
    return this.prefs.fontSize() + "px";
  }

  @HostBinding("style.font-size")
  get hostBaseFontSize(): string {
    return this.prefs.fontSize() + "px";
  }

  @HostBinding("class.reader-theme-dark")
  get hostDarkTheme(): boolean {
    return this.prefs.readerTheme() === "dark";
  }

  readonly tabs = signal<TabModule[]>([]);
  readonly passage = signal<ChapterResponse | null>(null);
  readonly activeVerse = signal<number | null>(null);
  readonly showSearch = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly passageTitle = computed(() => {
    const loc = this.navState.location();
    if (!loc) return "";
    return `${loc.book} ${loc.chapter}`;
  });

  readonly activeTab = computed(
    () =>
      this.tabs().find((t) => t.moduleId === this.navState.moduleId()) ?? null,
  );

  private readonly location$ = toObservable(this.navState.location).pipe(
    distinctUntilChanged(
      (a, b) =>
        a?.moduleId === b?.moduleId &&
        a?.book === b?.book &&
        a?.chapter === b?.chapter,
    ),
  );

  private readonly module$ = toObservable(this.navState.location).pipe(
    distinctUntilChanged((a, b) => a?.moduleId === b?.moduleId),
  );

  ngOnInit(): void {
    // Load all bible modules + their details in parallel for proper tab labels
    this.resourcesService.getBibles().subscribe({
      next: (mods) => {
        // Start with moduleId as label, then enrich with details
        const initial: TabModule[] = mods.map((m) => ({
          moduleId: m.moduleId,
          label: m.moduleId,
          title: m.name,
          hasStrongs: false,
        }));
        this.tabs.set(initial);

        // Fetch details for all modules in parallel
        forkJoin(
          mods.map((m) => this.resourcesService.getBibleDetails(m.moduleId)),
        ).subscribe({
          next: (details) => {
            const enriched: TabModule[] = mods.map((m, i) => {
              const d = details[i] as BibleModuleDetails;
              return {
                moduleId: m.moduleId,
                label: d.translation ?? m.moduleId,
                title: d.title ?? m.name,
                hasStrongs: d.hasStrongs ?? false,
              };
            });
            this.tabs.set(enriched);
          },
        });
      },
      error: () => this.error.set("Could not load Bible modules."),
    });

    // Update hasStrongs when module changes
    this.module$.subscribe((loc) => {
      if (!loc) return;
      const tab = this.tabs().find((t) => t.moduleId === loc.moduleId);
      this.navState.setHasStrongs(tab?.hasStrongs ?? false);
    });

    // Load passage when module/book/chapter changes
    this.location$
      .pipe(
        switchMap((loc) => {
          if (!loc) return EMPTY;
          this.loading.set(true);
          this.error.set(null);
          this.activeVerse.set(null);
          return this.bibleService
            .getChapter(loc.moduleId, loc.book, loc.chapter)
            .pipe(
              catchError(() => {
                this.error.set("Failed to load passage.");
                this.loading.set(false);
                return EMPTY;
              }),
            );
        }),
        tap(() => this.loading.set(false)),
      )
      .subscribe((passage) => {
        // Deduplicate verses by verse number — some modules have duplicate rows
        const seen = new Set<number>();
        const unique = passage.verses.filter((v) => {
          if (seen.has(v.verse)) return false;
          seen.add(v.verse);
          return true;
        });
        const deduped = { ...passage, verses: unique };
        this.passage.set(deduped);
        this.navState.setVerses(deduped.verses);
      });
  }

  onModuleTabClick(tab: TabModule): void {
    const loc = this.navState.location();
    if (!loc) return;
    this.navState.setHasStrongs(tab.hasStrongs);
    this.navState.navigate({ ...loc, moduleId: tab.moduleId, verse: null });
  }

  onVerseClick(verse: Verse): void {
    const already = this.activeVerse() === verse.verse;
    const newVerse = already ? null : verse.verse;
    this.activeVerse.set(newVerse);
    const loc = this.navState.location();
    if (!loc) return;
    this.navState.navigate({ ...loc, verse: newVerse });
  }

  onVerseDoubleClick(event: MouseEvent): void {
    const selected = window.getSelection()?.toString().trim() ?? "";
    const word = selected || this.wordAtPoint(event);
    if (!word) return;
    const clean = word.replace(/[^a-zA-Z'-]/g, "").toLowerCase();
    if (!clean) return;

    const verseNum = this.verseNumberAt(event);
    const verseData = this.passage()?.verses.find((v) => v.verse === verseNum);
    const strongs = this.findStrongs(clean, verseData?.strongsWords ?? []);
    console.debug('[strongs]', { clean, verseNum, strongsWords: verseData?.strongsWords, strongs });
    this.wordSelection.select(clean, strongs);
  }

  private findStrongs(
    word: string,
    strongsWords: StrongsWord[],
  ): string | null {
    const match = strongsWords.find((sw) =>
      sw.word
        .toLowerCase()
        .split(/\s+/)
        .some((w) => w.replace(/[^a-zA-Z'-]/g, "") === word),
    );
    return match?.number ?? null;
  }

  private verseNumberAt(event: MouseEvent): number {
    const row = (event.target as HTMLElement).closest(".v-row");
    const vn = row?.querySelector(".vn")?.textContent?.trim();
    return vn ? parseInt(vn, 10) : 1;
  }

  private wordAtPoint(event: MouseEvent): string {
    if (document.caretRangeFromPoint) {
      const range = document.caretRangeFromPoint(event.clientX, event.clientY);
      return range?.toString().trim() ?? "";
    }
    return "";
  }

  toggleSearch(): void {
    this.showSearch.update((v) => !v);
  }
  closeSearch(): void {
    this.showSearch.set(false);
  }

  trackByVerse(_: number, v: Verse): number {
    return v.verse;
  }
}
