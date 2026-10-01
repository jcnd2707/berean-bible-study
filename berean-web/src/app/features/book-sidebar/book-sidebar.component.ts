import { Component, OnInit, inject, signal, computed } from "@angular/core";
import { CommonModule } from "@angular/common";
import { BibleService } from "../../core/services/bible.service";
import { ResourcesService } from "../../core/services/resources.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { BookEntry } from "../../core/models";
import { isBookComplete } from "../../core/services/reading-progress";

@Component({
  selector: "app-book-sidebar",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./book-sidebar.component.html",
  styleUrl: "./book-sidebar.component.scss",
})
export class BookSidebarComponent implements OnInit {
  private readonly bibleService = inject(BibleService);
  private readonly resourcesService = inject(ResourcesService);
  readonly nav = inject(NavigationStateService);

  readonly books = signal<BookEntry[]>([]);
  readonly moduleId = signal<string>("");

  readonly otBooks = computed(() => this.books().filter((b) => b.number <= 39));
  readonly ntBooks = computed(() => this.books().filter((b) => b.number >= 40));

  readonly selectedBook = computed(() => {
    const abbr = this.nav.book();
    return this.books().find((b) => b.abbreviation === abbr) ?? null;
  });

  readonly chapterNumbers = computed(() => {
    const count = this.selectedBook()?.chapterCount ?? 0;
    return Array.from({ length: count }, (_, i) => i + 1);
  });

  ngOnInit(): void {
    this.resourcesService.getBibles().subscribe({
      next: (mods) => {
        if (!mods.length) return;
        const first = mods[0];
        this.moduleId.set(first.moduleId);
        this.loadBooks(first.moduleId);
      },
    });
  }

  private loadBooks(moduleId: string): void {
    this.bibleService.getBooks(moduleId).subscribe({
      next: (books) => {
        this.books.set(books);
        this.nav.setBooks(books);
      },
    });
  }

  selectBook(book: BookEntry): void {
    const current = this.nav.location();
    this.nav.setMaxChapter(book.chapterCount);
    this.nav.navigate({
      moduleId: current?.moduleId ?? this.moduleId(),
      book: book.abbreviation,
      chapter: 1,
      verse: null,
    });
  }

  selectChapter(ch: number): void {
    const current = this.nav.location();
    if (!current) return;
    this.nav.navigate({ ...current, chapter: ch, verse: null });
  }

  isActiveBook(book: BookEntry): boolean {
    return this.nav.book() === book.abbreviation;
  }

  isActiveChapter(ch: number): boolean {
    return this.nav.chapter() === ch;
  }

  hasRead(ch: number): boolean {
    const book = this.selectedBook();
    if (!book) return false;
    return this.nav.hasReadChapter(book.number, ch);
  }

  isBookRead(book: BookEntry): boolean {
    return isBookComplete(this.nav.readChapters(), book.number, book.chapterCount);
  }

  hasNote(ch: number): boolean {
    const book = this.selectedBook();
    if (!book) return false;
    return this.nav.hasNoteForChapter(book.abbreviation, ch);
  }
}
