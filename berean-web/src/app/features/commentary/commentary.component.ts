import {
  Component,
  OnInit,
  inject,
  signal,
  ViewChild,
  ElementRef,
  effect,
  untracked,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { toObservable } from "@angular/core/rxjs-interop";
import { switchMap, distinctUntilChanged, catchError } from "rxjs/operators";
import { EMPTY } from "rxjs";

import { CommentaryService } from "../../core/services/commentary.service";
import { ResourcesService } from "../../core/services/resources.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { CommentaryModule, CommentaryEntry } from "../../core/models";

@Component({
  selector: "app-commentary",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./commentary.component.html",
  styleUrl: "./commentary.component.scss",
})
export class CommentaryComponent implements OnInit {
  private readonly commentaryService = inject(CommentaryService);
  private readonly resourcesService = inject(ResourcesService);
  readonly nav = inject(NavigationStateService);

  @ViewChild("commBody") commBodyRef!: ElementRef<HTMLElement>;

  readonly modules = signal<CommentaryModule[]>([]);
  readonly activeModuleId = signal<string>("");
  readonly entries = signal<CommentaryEntry[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  private readonly location$ = toObservable(this.nav.location).pipe(
    distinctUntilChanged(
      (a, b) =>
        a?.book === b?.book &&
        a?.chapter === b?.chapter &&
        a?.moduleId === b?.moduleId,
    ),
  );

  // Scroll and push active commentary to nav state on verse change
  private readonly _verseEffect = effect(() => {
    const verse = this.nav.verse();
    requestAnimationFrame(() => {
      this.scrollToActiveEntry();
      this.pushActiveCommentary();
    });
  });

  // A chat citation can ask for a particular commentary module.
  private readonly _moduleRequest = effect(() => {
    const moduleId = this.nav.requestedCommentaryModule();
    if (!moduleId) return;
    this.nav.clearRequestedCommentaryModule();
    untracked(() => this.selectModule(moduleId));
  });

  ngOnInit(): void {
    this.resourcesService.getCommentaries().subscribe({
      next: (mods) => {
        this.modules.set(mods);
        if (mods.length > 0) this.activeModuleId.set(mods[0].moduleId);
      },
    });

    this.location$
      .pipe(
        switchMap((loc) => {
          const modId = this.activeModuleId();
          if (!loc || !modId) return EMPTY;
          this.loading.set(true);
          this.error.set(null);
          return this.commentaryService
            .getChapter(modId, loc.book, loc.chapter)
            .pipe(
              catchError(() => {
                this.error.set("No commentary available for this passage.");
                this.loading.set(false);
                return EMPTY;
              }),
            );
        }),
      )
      .subscribe((resp) => {
        if (resp.moduleId && resp.moduleId !== this.activeModuleId()) return; // a newer module was chosen meanwhile
        this.entries.set(resp.entries);
        this.loading.set(false);
        requestAnimationFrame(() => {
          this.scrollToActiveEntry();
          this.pushActiveCommentary();
        });
      });
  }

  selectModule(moduleId: string): void {
    this.activeModuleId.set(moduleId);
    this.entries.set([]);
    const loc = this.nav.location();
    if (!loc) return;
    this.loading.set(true);
    this.error.set(null);
    this.commentaryService
      .getChapter(moduleId, loc.book, loc.chapter)
      .subscribe({
        next: (resp) => {
          if (moduleId !== this.activeModuleId()) return;
          this.entries.set(resp.entries);
          this.loading.set(false);
          requestAnimationFrame(() => {
            this.scrollToActiveEntry();
            this.pushActiveCommentary();
          });
        },
        error: () => {
          this.error.set("No commentary available.");
          this.loading.set(false);
        },
      });
  }

  isActiveEntry(entry: CommentaryEntry): boolean {
    const v = this.nav.verse();
    if (v === null) return false;
    return v >= entry.verseBegin && v <= entry.verseEnd;
  }

  private pushActiveCommentary(): void {
    const verse = this.nav.verse();
    if (verse === null) {
      this.nav.setActiveCommentary(null, "");
      return;
    }
    const active =
      this.entries().find(
        (e) => verse >= e.verseBegin && verse <= e.verseEnd,
      ) ?? null;
    this.nav.setActiveCommentary(active, this.activeModuleId());
  }

  private scrollToActiveEntry(): void {
    const body = this.commBodyRef?.nativeElement;
    if (!body) return;
    const active = body.querySelector<HTMLElement>(".comm-entry--active");
    if (!active) return;
    active.scrollIntoView({ behavior: "smooth", block: "start" });
  }

  trackByEntry(_: number, e: CommentaryEntry): string {
    return e.reference;
  }
}
