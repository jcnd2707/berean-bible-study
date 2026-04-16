import {
  Component,
  OnInit,
  inject,
  signal,
  computed,
  effect,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { finalize } from "rxjs";

import { BibleService } from "../../core/services/bible.service";
import { ResourcesService } from "../../core/services/resources.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import {
  BibleModule,
  BookEntry,
  ChapterResponse,
  Verse,
} from "../../core/models";

@Component({
  selector: "app-bible-reader",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./bible-reader.component.html",
  styleUrl: "./bible-reader.component.scss",
})
export class BibleReaderComponent implements OnInit {
  private readonly bibleService = inject(BibleService);
  private readonly resourcesService = inject(ResourcesService);
  readonly navState = inject(NavigationStateService);

  // ── Module list ───────────────────────────────────────────────────────────
  readonly modules = signal<BibleModule[]>([]);
  readonly selectedModule = signal<BibleModule | null>(null);

  // ── Book list ─────────────────────────────────────────────────────────────
  readonly books = signal<BookEntry[]>([]);
  readonly selectedBook = signal<BookEntry | null>(null);

  // Grouped for the picker — OT: books 1-39, NT: 40+
  readonly otBooks = computed(() => this.books().filter((b) => b.number <= 39));
  readonly ntBooks = computed(() => this.books().filter((b) => b.number >= 40));

  // ── Chapter ───────────────────────────────────────────────────────────────
  readonly selectedChapter = signal<number>(1);
  readonly chapterCount = computed(
    () => this.selectedBook()?.chapterCount ?? 1,
  );
  readonly chapterNumbers = computed(() =>
    Array.from({ length: this.chapterCount() }, (_, i) => i + 1),
  );

  // ── Passage data ──────────────────────────────────────────────────────────
  readonly passage = signal<ChapterResponse | null>(null);
  readonly activeVerse = signal<number | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  ngOnInit(): void {
    this.resourcesService.getBibles().subscribe({
      next: (mods) => {
        this.modules.set(mods);
        if (mods.length > 0) this.onModuleChange(mods[0]);
      },
      error: () => this.error.set("Could not load Bible modules from the API."),
    });
  }

  // ── Event handlers ────────────────────────────────────────────────────────

  onModuleChange(mod: BibleModule): void {
    this.selectedModule.set(mod);
    this.books.set([]);
    this.selectedBook.set(null);
    this.passage.set(null);

    this.bibleService.getBooks(mod.moduleId).subscribe({
      next: (books) => {
        this.books.set(books);
        if (books.length > 0) this.onBookChange(books[0]);
      },
      error: () => this.error.set("Could not load book list."),
    });
  }

  onModuleSelect(event: Event): void {
    const id = (event.target as HTMLSelectElement).value;
    const mod = this.modules().find((m) => m.moduleId === id);
    if (mod) this.onModuleChange(mod);
  }

  onBookChange(book: BookEntry): void {
    this.selectedBook.set(book);
    this.selectedChapter.set(1);
    this.activeVerse.set(null);
    this.loadChapter();
  }

  onBookSelect(event: Event): void {
    const abbr = (event.target as HTMLSelectElement).value;
    const book = this.books().find((b) => b.abbreviation === abbr);
    if (book) this.onBookChange(book);
  }

  onChapterSelect(event: Event): void {
    const ch = parseInt((event.target as HTMLSelectElement).value, 10);
    this.selectedChapter.set(ch);
    this.activeVerse.set(null);
    this.loadChapter();
  }

  onVerseClick(verse: Verse): void {
    const already = this.activeVerse() === verse.verse;
    const newVerse = already ? null : verse.verse;
    this.activeVerse.set(newVerse);

    const mod = this.selectedModule();
    const book = this.selectedBook();
    if (!mod || !book) return;

    this.navState.navigate({
      moduleId: mod.moduleId,
      book: book.abbreviation,
      chapter: this.selectedChapter(),
      verse: newVerse,
    });
  }

  prevChapter(): void {
    if (this.selectedChapter() <= 1) return;
    this.selectedChapter.update((c) => c - 1);
    this.activeVerse.set(null);
    this.loadChapter();
  }

  nextChapter(): void {
    if (this.selectedChapter() >= this.chapterCount()) return;
    this.selectedChapter.update((c) => c + 1);
    this.activeVerse.set(null);
    this.loadChapter();
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private loadChapter(): void {
    const mod = this.selectedModule();
    const book = this.selectedBook();
    if (!mod || !book) return;

    this.loading.set(true);
    this.error.set(null);

    this.bibleService
      .getChapter(mod.moduleId, book.abbreviation, this.selectedChapter())
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (passage) => {
          this.passage.set(passage);
          this.navState.navigate({
            moduleId: mod.moduleId,
            book: book.abbreviation,
            chapter: this.selectedChapter(),
            verse: null,
          });
        },
        error: () =>
          this.error.set("Failed to load passage. Check the API is running."),
      });
  }

  trackByVerse(_: number, v: Verse): number {
    return v.verse;
  }
}
