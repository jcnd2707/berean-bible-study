import {
  Component,
  inject,
  signal,
  computed,
  output,
  OnInit,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import {
  debounceTime,
  distinctUntilChanged,
  switchMap,
  catchError,
} from "rxjs/operators";
import { Subject, EMPTY } from "rxjs";

import { BibleService } from "../../core/services/bible.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { SearchResult, Testament, BookEntry } from "../../core/models";

@Component({
  selector: "app-search-panel",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./search-panel.component.html",
  styleUrl: "./search-panel.component.scss",
})
export class SearchPanelComponent implements OnInit {
  private readonly bibleService = inject(BibleService);
  readonly nav = inject(NavigationStateService);

  // Emitted when user clicks a result — parent hides the search panel
  readonly navigate = output<void>();

  readonly query = signal("");
  readonly testament = signal<Testament>("both");
  readonly scopeBook = signal<string>(""); // empty = whole Bible
  readonly results = signal<SearchResult[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly searched = signal(false); // has a search been run yet?
  readonly books = signal<BookEntry[]>([]);

  readonly resultCount = computed(() => this.results().length);

  readonly otBooks = computed(() => this.books().filter((b) => b.number <= 39));
  readonly ntBooks = computed(() => this.books().filter((b) => b.number >= 40));

  // Group results by book for display
  readonly groupedResults = computed(() => {
    const map = new Map<string, SearchResult[]>();
    for (const r of this.results()) {
      const key = r.bookName;
      if (!map.has(key)) map.set(key, []);
      map.get(key)!.push(r);
    }
    return Array.from(map.entries()).map(([book, verses]) => ({
      book,
      verses,
    }));
  });

  private readonly search$ = new Subject<string>();

  ngOnInit(): void {
    // Load book list for the scope filter using the active module
    const moduleId = this.nav.moduleId();
    if (moduleId) {
      this.bibleService.getBooks(moduleId).subscribe({
        next: (books) => this.books.set(books),
      });
    }

    // Debounced search — fires 400ms after user stops typing
    this.search$
      .pipe(
        debounceTime(400),
        distinctUntilChanged(),
        switchMap((q) => {
          if (q.trim().length < 2) {
            this.results.set([]);
            this.searched.set(false);
            this.loading.set(false);
            return EMPTY;
          }
          this.loading.set(true);
          this.error.set(null);
          const moduleId = this.nav.moduleId();
          if (!moduleId) return EMPTY;
          return this.bibleService
            .search(moduleId, {
              q: q.trim(),
              limit: 200,
              testament: this.testament(),
              book: this.scopeBook() || undefined,
            })
            .pipe(
              catchError((err) => {
                const msg =
                  err?.error?.error ?? "Search failed. Check the API.";
                this.error.set(msg);
                this.loading.set(false);
                return EMPTY;
              }),
            );
        }),
      )
      .subscribe((results) => {
        this.results.set(results);
        this.loading.set(false);
        this.searched.set(true);
      });
  }

  onQueryInput(value: string): void {
    this.query.set(value);
    this.search$.next(value);
  }

  onFilterChange(): void {
    const q = this.query();
    if (q.trim().length >= 2) this.search$.next(q + " "); // force re-emit
  }

  onResultClick(result: SearchResult): void {
    const loc = this.nav.location();
    if (!loc) return;
    // Find the book abbreviation from our books list
    const book = this.books().find((b) => b.number === result.book);
    if (!book) return;
    this.nav.navigate({
      moduleId: loc.moduleId,
      book: book.abbreviation,
      chapter: result.chapter,
      verse: result.verse,
    });
    this.navigate.emit();
  }

  highlight(text: string): string {
    const q = this.query().trim();
    if (!q) return text;
    const escaped = q.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    return text.replace(new RegExp(`(${escaped})`, "gi"), "<mark>$1</mark>");
  }

  clearScope(): void {
    this.scopeBook.set("");
    this.testament.set("both");
    this.onFilterChange();
  }
}
