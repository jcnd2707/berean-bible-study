import {
  Component,
  OnInit,
  OnDestroy,
  inject,
  signal,
  computed,
  effect,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import {
  debounceTime,
  distinctUntilChanged,
  switchMap,
  catchError,
} from "rxjs/operators";
import { Subject, EMPTY, of } from "rxjs";

import { NotesService } from "../../core/services/notes.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";

@Component({
  selector: "app-notes",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./notes.component.html",
  styleUrl: "./notes.component.scss",
})
export class NotesComponent implements OnInit, OnDestroy {
  private readonly notesService = inject(NotesService);
  readonly nav = inject(NavigationStateService);

  readonly noteText = signal<string>("");
  readonly savedText = signal<string>("");
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly deleting = signal(false);
  readonly error = signal<string | null>(null);
  readonly saveStatus = signal<"idle" | "saved" | "error">("idle");

  readonly reference = computed(() => {
    const loc = this.nav.location();
    if (!loc) return null;
    return NotesService.toReference(loc.book, loc.chapter, loc.verse);
  });

  readonly referenceLabel = computed(() => {
    const loc = this.nav.location();
    if (!loc) return "No passage selected";
    const verse = loc.verse ? `:${loc.verse}` : "";
    return `${loc.book} ${loc.chapter}${verse}`;
  });

  readonly isDirty = computed(() => this.noteText() !== this.savedText());
  readonly hasNote = computed(() => this.savedText().trim().length > 0);

  private readonly save$ = new Subject<string>();

  private readonly _locationEffect = effect(() => {
    const ref = this.reference();
    if (!ref) return;
    this.loadNote(ref);
  });

  ngOnInit(): void {
    this.refreshNotedRefs();

    this.save$
      .pipe(
        debounceTime(1000),
        distinctUntilChanged(),
        switchMap((text) => {
          const ref = this.reference();
          if (!ref || !text.trim()) return EMPTY;
          this.saving.set(true);
          this.saveStatus.set("idle");
          return this.notesService.upsert(ref, text).pipe(
            catchError(() => {
              this.saveStatus.set("error");
              this.saving.set(false);
              return EMPTY;
            }),
          );
        }),
      )
      .subscribe((saved) => {
        this.savedText.set(saved.text);
        this.saving.set(false);
        this.saveStatus.set("saved");
        this.refreshNotedRefs();
        setTimeout(() => this.saveStatus.set("idle"), 2000);
      });
  }

  onTextInput(value: string): void {
    this.noteText.set(value);
    this.save$.next(value);
  }

  /**
   * The panel this lives in now keeps it mounted rather than destroying it
   * on a tab/view switch (MOBILE_PLAN.md §1), so the 1s debounce above
   * normally has time to fire on its own. This is a backstop for the paths
   * that still can destroy it — e.g. closing the app — so a keystroke made
   * in the last second before that isn't silently dropped.
   */
  ngOnDestroy(): void {
    const ref = this.reference();
    const text = this.noteText();
    if (ref && this.isDirty() && text.trim()) {
      this.notesService.upsert(ref, text).subscribe();
    }
  }

  deleteNote(): void {
    const ref = this.reference();
    if (!ref || !this.hasNote()) return;
    this.deleting.set(true);
    this.notesService.delete(ref).subscribe({
      next: () => {
        this.noteText.set("");
        this.savedText.set("");
        this.deleting.set(false);
        this.saveStatus.set("idle");
        this.refreshNotedRefs();
      },
      error: () => {
        this.error.set("Failed to delete note.");
        this.deleting.set(false);
      },
    });
  }

  private refreshNotedRefs(): void {
    this.notesService.getAll().subscribe({
      next: (notes) =>
        this.nav.setNotedReferences(notes.map((n) => n.reference)),
    });
  }

  private loadNote(reference: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.noteText.set("");
    this.savedText.set("");

    this.notesService
      .getNote(reference)
      .pipe(
        catchError((err) => {
          if (err.status === 404) return of(null);
          this.error.set("Failed to load note.");
          return of(null);
        }),
      )
      .subscribe((note) => {
        const text = note?.text ?? "";
        this.noteText.set(text);
        this.savedText.set(text);
        this.loading.set(false);
      });
  }
}
