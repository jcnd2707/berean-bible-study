import {
  Component,
  OnInit,
  OnDestroy,
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
  readonly layout = inject(LayoutService);

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
   * instead — which brings up the native copy/share toolbar, and that
   * toolbar renders on top of any custom UI, covering it. Rather than try
   * to coexist with it, `.verse-list` disables native text selection
   * entirely under `(pointer: coarse)` (see the .scss), and this detects
   * the long-press itself via Pointer Events, finding the word under the
   * finger the same way `onVerseDoubleClick` does on desktop
   * (caretRangeFromPoint) but expanded manually since a caret has no
   * selection to read text from. Desktop keeps `onVerseDoubleClick`
   * unchanged; this only runs for a touch pointer.
   */
  readonly touchLookup = signal<{
    word: string;
    strongs: string | null;
    x: number;
    y: number;
  } | null>(null);

  private touchStart: { x: number; y: number } | null = null;
  private longPressTimer: ReturnType<typeof setTimeout> | null = null;
  private longPressFired = false;
  private readonly LONG_PRESS_MS = 500;
  private readonly MOVE_TOLERANCE = 10;

  onVerseListPointerDown(e: PointerEvent): void {
    this.touchLookup.set(null);
    if (!this.layout.coarsePointer() || e.pointerType !== "touch") return;
    this.touchStart = { x: e.clientX, y: e.clientY };
    this.longPressFired = false;
    const x = e.clientX;
    const y = e.clientY;
    this.longPressTimer = setTimeout(
      () => this.fireLongPress(x, y),
      this.LONG_PRESS_MS,
    );
  }

  onVerseListPointerMove(e: PointerEvent): void {
    if (!this.touchStart) return;
    const dx = e.clientX - this.touchStart.x;
    const dy = e.clientY - this.touchStart.y;
    if (Math.hypot(dx, dy) > this.MOVE_TOLERANCE) {
      this.cancelLongPressTimer();
    }
  }

  /**
   * Chapter swipe (MOBILE_PLAN.md §4.3, phase 5, optional): a horizontal
   * drag that ends before the long-press timer fires goes to the
   * previous/next chapter instead, ignored when vertical movement
   * dominates so it doesn't fight a normal scroll.
   */
  onVerseListPointerUp(e: PointerEvent): void {
    this.cancelLongPressTimer();
    const start = this.touchStart;
    this.touchStart = null;
    if (!start || this.longPressFired) return;
    const dx = e.clientX - start.x;
    const dy = e.clientY - start.y;
    if (Math.abs(dx) < 60 || Math.abs(dx) < Math.abs(dy) * 1.5) return;
    if (dx < 0) {
      this.navState.nextChapter();
    } else {
      this.navState.prevChapter();
    }
  }

  private cancelLongPressTimer(): void {
    if (this.longPressTimer !== null) {
      clearTimeout(this.longPressTimer);
      this.longPressTimer = null;
    }
  }

  private fireLongPress(x: number, y: number): void {
    this.longPressFired = true;
    const raw = this.wordAtPoint(x, y);
    const clean = raw.replace(/[^a-zA-Z'-]/g, "").toLowerCase();
    if (!clean) return;
    const row = document.elementFromPoint(x, y)?.closest(".v-row");
    const vn = row?.querySelector(".vn")?.textContent?.trim();
    const verseNum = vn ? parseInt(vn, 10) : null;
    const verseData = verseNum
      ? this.passage()?.verses.find((v) => v.verse === verseNum)
      : undefined;
    const strongs = this.findStrongs(clean, verseData?.strongsWords ?? []);
    this.touchLookup.set({ word: clean, strongs, x, y });
  }

  confirmTouchLookup(): void {
    const lookup = this.touchLookup();
    if (!lookup) return;
    this.wordSelection.select(lookup.word, lookup.strongs);
    this.touchLookup.set(null);
  }

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

  ngOnDestroy(): void {
    this.cancelLongPressTimer();
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
    const word = selected || this.wordAtPoint(event.clientX, event.clientY);
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

  /**
   * The whole word at a viewport point, found by expanding outward from the
   * caret position `caretRangeFromPoint` gives — that range is collapsed
   * (zero-width), so `.toString()` on it alone is always empty; there's no
   * selection to read text from when nothing was selected to begin with
   * (the long-press lookup above disables selection entirely).
   */
  private wordAtPoint(x: number, y: number): string {
    if (!document.caretRangeFromPoint) return "";
    const range = document.caretRangeFromPoint(x, y);
    const node = range?.startContainer;
    if (!node || node.nodeType !== Node.TEXT_NODE) return "";
    const text = node.textContent ?? "";
    const isWordChar = (c: string) => /[A-Za-z']/.test(c);
    let start = range!.startOffset;
    let end = start;
    while (start > 0 && isWordChar(text[start - 1])) start--;
    while (end < text.length && isWordChar(text[end])) end++;
    return text.slice(start, end);
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
