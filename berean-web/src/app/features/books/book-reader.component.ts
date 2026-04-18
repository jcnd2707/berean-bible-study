import {
  Component, OnInit, inject, signal, computed,
  ViewChild, ElementRef,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { catchError, debounceTime, distinctUntilChanged, switchMap } from 'rxjs/operators';
import { Subject, EMPTY } from 'rxjs';

import { BooksService } from '../../core/services/books.service';
import { PreferencesService } from '../../core/services/preferences.service';
import {
  BookSummary, BookChapter, BookParagraph,
} from '../../core/models';

@Component({
  selector: 'app-book-reader',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './book-reader.component.html',
  styleUrl: './book-reader.component.scss',
})
export class BookReaderComponent implements OnInit {
  private readonly booksService = inject(BooksService);
  readonly prefs                 = inject(PreferencesService);

  @ViewChild('readingPane') readingPaneRef!: ElementRef<HTMLElement>;

  // ── Catalog ───────────────────────────────────────────────────────────────
  readonly books          = signal<BookSummary[]>([]);
  readonly activeBook     = signal<BookSummary | null>(null);
  readonly showBookPicker = signal(false);

  // ── Chapters ──────────────────────────────────────────────────────────────
  readonly chapters        = signal<BookChapter[]>([]);
  readonly activeChapterId = signal<number | null>(null);
  readonly activeChapterIdx = computed(() => {
    const id = this.activeChapterId();
    return this.chapters().findIndex(c => c.id === id);
  });
  readonly canGoPrev = computed(() => this.activeChapterIdx() > 0);
  readonly canGoNext = computed(() => this.activeChapterIdx() < this.chapters().length - 1);

  // ── Content ───────────────────────────────────────────────────────────────
  readonly paragraphs    = signal<BookParagraph[]>([]);
  readonly activeChapter = computed(() =>
    this.chapters().find(c => c.id === this.activeChapterId()) ?? null
  );

  // ── Search ────────────────────────────────────────────────────────────────
  readonly showSearch   = signal(false);
  readonly searchQuery  = signal('');
  readonly searchResults = signal<{ chapterTitle: string; snippet: string; chapterId: number }[]>([]);
  readonly searching    = signal(false);

  // ── States ────────────────────────────────────────────────────────────────
  readonly loadingChapters = signal(false);
  readonly loadingContent  = signal(false);
  readonly error           = signal<string | null>(null);

  private readonly search$ = new Subject<string>();

  ngOnInit(): void {
    this.booksService.getAll().subscribe({
      next: books => this.books.set(books),
      error: () => this.error.set('Could not load book catalog.'),
    });

    // Debounced in-book search
    this.search$.pipe(
      debounceTime(400),
      distinctUntilChanged(),
      switchMap(q => {
        const book = this.activeBook();
        if (!q.trim() || !book) { this.searchResults.set([]); return EMPTY; }
        this.searching.set(true);
        return this.booksService.search(book.moduleId, q).pipe(
          catchError(() => { this.searching.set(false); return EMPTY; })
        );
      })
    ).subscribe(results => {
      // Flatten to a result list — each chapter with a snippet
      const flat = results.map(r => ({
        chapterId: r.chapter.id,
        chapterTitle: r.chapter.title,
        snippet: r.paragraphs[0]?.content ?? '',
      }));
      this.searchResults.set(flat);
      this.searching.set(false);
    });
  }

  selectBook(book: BookSummary): void {
    this.activeBook.set(book);
    this.showBookPicker.set(false);
    this.chapters.set([]);
    this.paragraphs.set([]);
    this.activeChapterId.set(null);
    this.error.set(null);
    this.loadingChapters.set(true);

    this.booksService.getChapters(book.moduleId).subscribe({
      next: chapters => {
        this.chapters.set(chapters);
        this.loadingChapters.set(false);
        if (chapters.length > 0) this.loadChapter(chapters[0].id);
      },
      error: () => {
        this.error.set('Could not load chapters.');
        this.loadingChapters.set(false);
      }
    });
  }

  loadChapter(chapterId: number): void {
    const book = this.activeBook();
    if (!book) return;
    this.activeChapterId.set(chapterId);
    this.loadingContent.set(true);
    this.error.set(null);

    this.booksService.getChapter(book.moduleId, chapterId).subscribe({
      next: content => {
        this.paragraphs.set(content.paragraphs);
        this.loadingContent.set(false);
        // Scroll reading pane to top
        requestAnimationFrame(() => {
          if (this.readingPaneRef?.nativeElement)
            this.readingPaneRef.nativeElement.scrollTop = 0;
        });
      },
      error: () => {
        this.error.set('Could not load chapter.');
        this.loadingContent.set(false);
      }
    });
  }

  prevChapter(): void {
    const idx = this.activeChapterIdx();
    if (idx > 0) this.loadChapter(this.chapters()[idx - 1].id);
  }

  nextChapter(): void {
    const idx = this.activeChapterIdx();
    if (idx < this.chapters().length - 1) this.loadChapter(this.chapters()[idx + 1].id);
  }

  onSearchInput(q: string): void {
    this.searchQuery.set(q);
    this.search$.next(q);
  }

  navigateToSearchResult(chapterId: number): void {
    this.loadChapter(chapterId);
    this.showSearch.set(false);
    this.searchQuery.set('');
    this.searchResults.set([]);
  }

  stripHtml(html: string): string {
    return html.replace(/<[^>]*>/g, '');
  }
}
