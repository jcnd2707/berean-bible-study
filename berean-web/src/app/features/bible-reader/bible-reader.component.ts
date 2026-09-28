import {
  Component,
  OnInit,
  OnDestroy,
  ElementRef,
  ViewChild,
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
import { LayoutService } from "../../core/services/layout.service";
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
export class BibleReaderComponent implements OnInit, OnDestroy {
  private readonly bibleService = inject(BibleService);
  private readonly resourcesService = inject(ResourcesService);
  readonly navState = inject(NavigationStateService);
  private readonly wordSelection = inject(WordSelectionService);
  readonly prefs = inject(PreferencesService);
  private readonly layout = inject(LayoutService);

  @ViewChild("verseList") verseListRef?: ElementRef<HTMLElement>;

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

  /**
   * Touch word lookup (MOBILE_PLAN.md §4.3): a coarse pointer can't
   * double-click, and a long-press starts Android's own text-selection UI
   * instead. Rather than fight that, this watches `selectionchange` for a
   * selection Android already made inside the verse list and offers to look
   * it up — leaving Android's native copy/share menu alone. Desktop keeps
   * `onVerseDoubleClick` below; this is additive, gated on coarse pointer so
   * a mouse drag-select on desktop never triggers it.
   */
  readonly touchLookup = signal<{ word: string; x: number; y: number } | null>(
    null,
  );
  private readonly onSelectionChange = () => this.checkSelectionForLookup();

  private checkSelectionForLookup(): void {
    if (!this.layout.coarsePointer()) {
      this.touchLookup.set(null);
      return;
    }
    const sel = window.getSelection();
    const verseListEl = this.verseListRef?.nativeElement;
    if (!sel || sel.isCollapsed || sel.rangeCount === 0 || !verseListEl) {
      this.touchLookup.set(null);
      return;
    }
    const range = sel.getRangeAt(0);
    if (!verseListEl.contains(range.commonAncestorContainer)) {
      this.touchLookup.set(null);
      return;
    }
    const firstWord = sel.toString().trim().split(/\s+/)[0] ?? "";
    const clean = firstWord.replace(/[^a-zA-Z'-]/g, "").toLowerCase();
    if (!clean) {
      this.touchLookup.set(null);
      return;
    }
    const rect = range.getBoundingClientRect();
    this.touchLookup.set({
      word: clean,
      x: rect.left + rect.width / 2,
      y: rect.top,
    });
  }

  confirmTouchLookup(): void {
    const lookup = this.touchLookup();
    if (!lookup) return;
    const sel = window.getSelection();
    let anchor: Element | null = null;
    if (sel && sel.rangeCount > 0) {
      const container = sel.getRangeAt(0).commonAncestorContainer;
      anchor =
        container.nodeType === Node.TEXT_NODE
          ? container.parentElement
          : (container as Element);
    }
    const row = anchor?.closest(".v-row");
    const vn = row?.querySelector(".vn")?.textContent?.trim();
    const verseNum = vn ? parseInt(vn, 10) : null;
    const verseData = verseNum
      ? this.passage()?.verses.find((v) => v.verse === verseNum)
      : undefined;
    const strongs = this.findStrongs(lookup.word, verseData?.strongsWords ?? []);
    this.wordSelection.select(lookup.word, strongs);
    this.touchLookup.set(null);
    window.getSelection()?.removeAllRanges();
  }

  ngOnInit(): void {
    document.addEventListener("selectionchange", this.onSelectionChange);

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

  ngOnDestroy(): void {
    document.removeEventListener("selectionchange", this.onSelectionChange);
  }

  onModuleTabClick(tab: TabModule): void {
    const loc = this.navState.location();
    if (!loc) return;
    this.navState.setHasStrongs(tab.hasStrongs);
    this.navState.navigate({ ...loc, moduleId: tab.moduleId, verse: null });
  }

  /** Show the Strong's number under each tagged word. */
  readonly showStrongs = signal(false);

  /**
   * Whether the chapter on screen carries Strong's tags. Judged from the verses themselves: a
   * module's own "has Strong's" flag isn't reliable (akjvstrong reports false).
   */
  readonly chapterHasStrongs = computed(
    () => this.passage()?.verses.some((v) => !!v.strongsWords?.length) ?? false,
  );

  /** Clicking a tagged word opens it in the dictionary panel by its Strong's number. */
  onStrongsWordClick(event: MouseEvent, word: StrongsWord): void {
    event.stopPropagation();
    const clean = word.word.replace(/[^a-zA-Z'-]/g, "").toLowerCase();
    this.wordSelection.select(clean || word.word, word.number);
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
