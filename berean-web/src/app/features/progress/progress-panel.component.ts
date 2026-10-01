import { Component, OnInit, computed, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { ResourcesService } from "../../core/services/resources.service";
import { BookProgress, formatReadDate, nextMilestone } from "../../core/services/reading-progress";

interface ChapterCell {
  chapter: number;
  read: boolean;
  title: string;
}

/**
 * "Reading progress" overlay (CHAPTER_PROGRESS_PLAN.md D7): how much of the Bible is marked read,
 * split by testament, with every book listed and a tap-to-open chapter grid showing exactly which
 * chapters are marked. Everything here is derived from the same read-chapters set the sidebar
 * dots use; it never writes to it.
 */
@Component({
  selector: "app-progress-panel",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./progress-panel.component.html",
  styleUrl: "./progress-panel.component.scss",
})
export class ProgressPanelComponent implements OnInit {
  readonly nav = inject(NavigationStateService);
  private readonly resourcesService = inject(ResourcesService);

  readonly progress = this.nav.readingProgress;
  readonly next = computed(() => nextMilestone(this.progress()));
  readonly otBooks = computed(() => this.progress().books.filter((b) => b.number <= 39));
  readonly ntBooks = computed(() => this.progress().books.filter((b) => b.number >= 40));

  /** The one book whose chapter grid is open. */
  readonly expandedBook = signal<number | null>(null);

  // Opening a chapter keeps the translation you're reading; with none open yet, the first module.
  private readonly defaultModuleId = signal<string | null>(null);

  ngOnInit(): void {
    if (this.nav.moduleId()) return;
    this.resourcesService.getBibles().subscribe({
      next: (mods) => this.defaultModuleId.set(mods[0]?.moduleId ?? null),
    });
  }

  toggleBook(book: BookProgress): void {
    this.expandedBook.update((open) => (open === book.number ? null : book.number));
  }

  chapterCells(book: BookProgress): ChapterCell[] {
    const cells: ChapterCell[] = [];
    for (let chapter = 1; chapter <= book.chapterCount; chapter++) {
      const readAt = this.nav.readAtForChapter(book.number, chapter);
      const date = readAt ? formatReadDate(readAt) : "";
      cells.push({
        chapter,
        read: readAt !== null,
        title: readAt
          ? `${book.name} ${chapter} — read${date ? " " + date : ""}`
          : `${book.name} ${chapter} — not read yet`,
      });
    }
    return cells;
  }

  openChapter(book: BookProgress, chapter: number): void {
    const moduleId = this.nav.moduleId() ?? this.defaultModuleId();
    if (!moduleId) return;

    this.nav.setMaxChapter(book.chapterCount);
    this.nav.navigate({ moduleId, book: book.abbreviation, chapter, verse: null });
    this.nav.closeProgress();
  }
}
