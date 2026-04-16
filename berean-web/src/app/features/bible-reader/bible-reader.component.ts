import { Component, OnInit, inject, signal, computed } from "@angular/core";
import { CommonModule } from "@angular/common";
import { toObservable } from "@angular/core/rxjs-interop";
import {
  switchMap,
  tap,
  catchError,
  distinctUntilChanged,
} from "rxjs/operators";
import { of, EMPTY } from "rxjs";

import { BibleService } from "../../core/services/bible.service";
import { ResourcesService } from "../../core/services/resources.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { BibleModule, ChapterResponse, Verse } from "../../core/models";

@Component({
  selector: "app-bible-reader",
  standalone: true,
  imports: [CommonModule],
  templateUrl: "./bible-reader.component.html",
  styleUrl: "./bible-reader.component.scss",
})
export class BibleReaderComponent implements OnInit {
  private readonly bibleService = inject(BibleService);
  private readonly resourcesService = inject(ResourcesService);
  readonly navState = inject(NavigationStateService);

  readonly modules = signal<BibleModule[]>([]);
  readonly passage = signal<ChapterResponse | null>(null);
  readonly activeVerse = signal<number | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly passageTitle = computed(() => {
    const loc = this.navState.location();
    if (!loc) return "";
    return `${loc.book} ${loc.chapter}`;
  });

  readonly activeModuleName = computed(() => {
    const id = this.navState.moduleId();
    return this.modules().find((m) => m.moduleId === id)?.name ?? id ?? "";
  });

  // Convert location signal to observable, only emit when module/book/chapter changes
  private readonly location$ = toObservable(this.navState.location).pipe(
    distinctUntilChanged(
      (a, b) =>
        a?.moduleId === b?.moduleId &&
        a?.book === b?.book &&
        a?.chapter === b?.chapter,
    ),
  );

  ngOnInit(): void {
    this.resourcesService.getBibles().subscribe({
      next: (mods) => this.modules.set(mods),
      error: () => this.error.set("Could not load Bible modules."),
    });

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
      .subscribe((passage) => this.passage.set(passage));
  }

  onModuleTabClick(mod: BibleModule): void {
    const loc = this.navState.location();
    if (!loc) return;
    this.navState.navigate({ ...loc, moduleId: mod.moduleId, verse: null });
  }

  onVerseClick(verse: Verse): void {
    const already = this.activeVerse() === verse.verse;
    const newVerse = already ? null : verse.verse;
    this.activeVerse.set(newVerse);
    const loc = this.navState.location();
    if (!loc) return;
    this.navState.navigate({ ...loc, verse: newVerse });
  }

  trackByVerse(_: number, v: Verse): number {
    return v.verse;
  }
}
