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
import { BackStackService } from "../../core/services/back-stack.service";
import { SpeechService } from "../../core/services/speech.service";
import { ClipboardService } from "../../core/services/clipboard";
import { ReadingProgressService } from "../../core/services/reading-progress.service";
import { formatReadDate } from "../../core/services/reading-progress";
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
import { SessionsListComponent } from "../sessions/sessions-list.component";
import { wordForSpeech, overrideKey } from "./speech-word";
import { formatVerseCitation } from "./verse-citation";

interface TabModule {
  moduleId: string;
  label: string; // short display label e.g. "BSB", "KJV"
  title: string; // full title for tooltip
  hasStrongs: boolean;
  language: string;
}

interface WordMenuEntry {
  word: string; // cleaned, ASCII/lowercase — for lookup and display
  speechWord: string; // accents and case kept — for Hear it
  strongs: string | null;
  lang: string;
  x: number;
  y: number;
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
    SessionsListComponent,
  ],
  templateUrl: "./bible-reader.component.html",
  styleUrl: "./bible-reader.component.scss",
})
export class BibleReaderComponent implements OnInit, OnDestroy {
  private readonly bibleService = inject(BibleService);
  private readonly resourcesService = inject(ResourcesService);
  readonly navState = inject(NavigationStateService);
  private readonly wordSelection = inject(WordSelectionService);
  private readonly backStack = inject(BackStackService);
  private readonly speech = inject(SpeechService);
  private readonly clipboard = inject(ClipboardService);
  private readonly readingProgress = inject(ReadingProgressService);
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
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly passageTitle = computed(() => {
    const loc = this.navState.location();
    if (!loc) return "";
    return `${loc.book} ${loc.chapter}`;
  });

  /** The book being read, for its canonical number — progress isn't tied to a translation's abbreviation. */
  readonly chapterBook = computed(() => {
    const abbr = this.navState.book();
    return this.navState.books().find((b) => b.abbreviation === abbr) ?? null;
  });

  readonly chapterReadAt = computed(() => {
    const book = this.chapterBook();
    const chapter = this.navState.chapter();
    if (!book || chapter === null) return null;
    return this.navState.readAtForChapter(book.number, chapter);
  });
  readonly chapterRead = computed(() => this.chapterReadAt() !== null);
  readonly readDate = computed(() => {
    const at = this.chapterReadAt();
    return at ? formatReadDate(at) : "";
  });
  readonly markingRead = signal(false);
  readonly readError = signal<string | null>(null);

  async toggleChapterRead(): Promise<void> {
    const book = this.chapterBook();
    const chapter = this.navState.chapter();
    if (!book || chapter === null || this.markingRead()) return;

    this.markingRead.set(true);
    this.readError.set(null);
    const ok = await this.readingProgress.setRead(book.number, chapter, !this.chapterRead());
    this.markingRead.set(false);
    if (!ok) this.readError.set("Couldn't save — try again.");
  }

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
  readonly wordMenu = signal<WordMenuEntry | null>(null);
  readonly wordMenuMessage = signal<string | null>(null);
  private readonly closeWordMenuFn = () => this.closeWordMenu();

  private openWordMenu(entry: WordMenuEntry): void {
    this.wordMenu.set(entry);
    this.wordMenuMessage.set(null);
    this.backStack.open(this.closeWordMenuFn);
  }

  /** Shared by Esc, the Android back button (via BackStackService), and tapping elsewhere. */
  closeWordMenu(): void {
    if (!this.wordMenu()) return;
    this.wordMenu.set(null);
    this.wordMenuMessage.set(null);
    this.backStack.close(this.closeWordMenuFn);
  }

  private touchStart: { x: number; y: number } | null = null;
  private longPressTimer: ReturnType<typeof setTimeout> | null = null;
  private longPressFired = false;
  private readonly LONG_PRESS_MS = 500;
  private readonly MOVE_TOLERANCE = 10;

  onVerseListPointerDown(e: PointerEvent): void {
    this.closeWordMenu();
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
    this.openWordMenu({
      word: clean,
      speechWord: wordForSpeech(raw),
      strongs,
      lang: this.activeTab()?.language ?? "en",
      x,
      y,
    });
  }

  /**
   * Desktop's half of the word menu (§D4): right-clicking a word shows the same two choices as
   * a long press. The browser's own context menu is kept when text is selected (so Copy still
   * works) or the pointer isn't over a word. Double-click (unchanged, below) still looks a word
   * up directly.
   */
  onVerseListContextMenu(event: MouseEvent): void {
    const selected = window.getSelection()?.toString().trim() ?? "";
    if (selected) return;
    const raw = this.wordAtPoint(event.clientX, event.clientY);
    const clean = raw.replace(/[^a-zA-Z'-]/g, "").toLowerCase();
    if (!clean) return;

    event.preventDefault();
    const verseNum = this.verseNumberAt(event);
    const verseData = this.passage()?.verses.find((v) => v.verse === verseNum);
    const strongs = this.findStrongs(clean, verseData?.strongsWords ?? []);
    this.openWordMenu({
      word: clean,
      speechWord: wordForSpeech(raw),
      strongs,
      lang: this.activeTab()?.language ?? "en",
      x: event.clientX,
      y: event.clientY,
    });
  }

  confirmWordLookup(): void {
    const menu = this.wordMenu();
    if (!menu) return;
    this.wordSelection.select(menu.word, menu.strongs);
    this.closeWordMenu();
  }

  /** Stays open afterward (§D4) so a second tap/click replays the word. */
  async hearWord(): Promise<void> {
    const menu = this.wordMenu();
    if (!menu) return;
    this.wordMenuMessage.set(null);
    const override = await this.speech.resolveOverride(menu.lang, overrideKey(menu.speechWord));
    const result = await this.speech.speak(override ?? menu.speechWord, { lang: menu.lang });
    if (!result.ok) this.wordMenuMessage.set(result.message ?? null);
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
          language: m.language,
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
                language: m.language,
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
          this.readError.set(null);
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
    if (this.copyMessageTimer !== null) clearTimeout(this.copyMessageTimer);
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

  readonly copyMessage = signal<string | null>(null);
  private copyMessageTimer: ReturnType<typeof setTimeout> | null = null;

  async copyVerse(): Promise<void> {
    const verseNum = this.activeVerse();
    const loc = this.navState.location();
    const p = this.passage();
    if (verseNum === null || !loc || !p) return;
    const verseData = p.verses.find((v) => v.verse === verseNum);
    if (!verseData) return;

    const citation = formatVerseCitation({
      html: verseData.text,
      bookName: p.bookName,
      chapter: loc.chapter,
      verse: verseNum,
      translation: this.activeTab()?.label ?? loc.moduleId,
    });
    const ok = await this.clipboard.copy(citation);

    if (this.copyMessageTimer !== null) clearTimeout(this.copyMessageTimer);
    this.copyMessage.set(ok ? `Copied ${loc.book} ${loc.chapter}:${verseNum}` : "Couldn't copy");
    this.copyMessageTimer = setTimeout(() => this.copyMessage.set(null), 2000);
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
   *
   * Unicode letters/marks (`\p{L}\p{M}`), not just `[A-Za-z']`, so an accented word like
   * "Jehová" is picked up whole instead of truncated at the accent (D5). Firefox has no
   * `caretRangeFromPoint`, only the standard `caretPositionFromPoint`.
   */
  private wordAtPoint(x: number, y: number): string {
    let node: Node | null = null;
    let offset = 0;

    if (document.caretRangeFromPoint) {
      const range = document.caretRangeFromPoint(x, y);
      node = range?.startContainer ?? null;
      offset = range?.startOffset ?? 0;
    } else if (typeof (document as any).caretPositionFromPoint === "function") {
      const pos = (document as any).caretPositionFromPoint(x, y);
      node = pos?.offsetNode ?? null;
      offset = pos?.offset ?? 0;
    }

    if (!node || node.nodeType !== Node.TEXT_NODE) return "";
    const text = node.textContent ?? "";
    const isWordChar = (c: string) => /[\p{L}\p{M}']/u.test(c);
    let start = offset;
    let end = start;
    while (start > 0 && isWordChar(text[start - 1])) start--;
    while (end < text.length && isWordChar(text[end])) end++;
    return text.slice(start, end);
  }

  trackByVerse(_: number, v: Verse): number {
    return v.verse;
  }
}
