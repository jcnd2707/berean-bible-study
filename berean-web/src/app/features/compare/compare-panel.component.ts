import {
  Component, OnInit, inject, signal, computed, effect,
  ViewChildren, QueryList, ElementRef, AfterViewInit,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { forkJoin, EMPTY } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { BibleService } from '../../core/services/bible.service';
import { NavigationStateService } from '../../core/services/navigation-state.service';
import { BibleModule, ChapterResponse, Verse } from '../../core/models';
import { ResourcesService } from '../../core/services/resources.service';
import { LayoutService } from '../../core/services/layout.service';

interface CompareColumn {
  moduleId: string;
  label: string;
  title: string;
  passage: ChapterResponse | null;
  loading: boolean;
  error: string | null;
}

interface InterleavedEntry {
  moduleId: string;
  label: string;
  text: string | null;
}

interface InterleavedRow {
  verse: number;
  entries: InterleavedEntry[];
}

@Component({
  selector: 'app-compare-panel',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './compare-panel.component.html',
  styleUrl: './compare-panel.component.scss',
})
export class ComparePanelComponent implements OnInit, AfterViewInit {
  private readonly bibleService     = inject(BibleService);
  private readonly resourcesService = inject(ResourcesService);
  readonly nav                       = inject(NavigationStateService);
  readonly layout                    = inject(LayoutService);

  @ViewChildren('colBody') colBodies!: QueryList<ElementRef<HTMLElement>>;

  readonly allModules     = signal<{ moduleId: string; label: string; title: string }[]>([]);
  readonly selectedIds    = signal<Set<string>>(new Set());
  readonly columns        = signal<CompareColumn[]>([]);
  readonly showPicker     = signal(false);
  readonly syncScroll     = signal(true);
  private isSyncing       = false;

  readonly activeVerse = computed(() => this.nav.verse());

  /**
   * MOBILE_PLAN.md §5: up to 4 side-by-side columns are unreadable below
   * desktop widths (~100px/column on a phone). Phone always interleaves;
   * tablet interleaves in portrait (no room for even 2 columns) and in
   * landscape once there are 3+ translations (2 still fit side by side).
   */
  readonly interleaved = computed(() => {
    const l = this.layout.layout();
    if (l === 'phone') return true;
    if (l === 'tablet') {
      if (this.layout.orientation() === 'portrait') return true;
      return this.columns().length >= 3;
    }
    return false;
  });

  readonly interleavedRows = computed<InterleavedRow[]>(() => {
    const cols = this.columns();
    const verseNums = new Set<number>();
    for (const c of cols) {
      for (const v of c.passage?.verses ?? []) verseNums.add(v.verse);
    }
    return Array.from(verseNums)
      .sort((a, b) => a - b)
      .map((verse) => ({
        verse,
        entries: cols.map((c) => ({
          moduleId: c.moduleId,
          label: c.label,
          text: c.passage?.verses.find((v) => v.verse === verse)?.text ?? null,
        })),
      }));
  });

  readonly anyLoading = computed(() => this.columns().some((c) => c.loading));

  // Reload columns when location (book/chapter) changes
  private readonly _locationEffect = effect(() => {
    const loc = this.nav.location();
    if (!loc) return;
    // Just trigger a reload for all currently selected modules
    this.loadAllColumns();
  });

  ngOnInit(): void {
    this.resourcesService.getBibles().subscribe({
      next: mods => {
        // Load details for proper labels
        forkJoin(mods.map(m => this.resourcesService.getBibleDetails(m.moduleId)
          .pipe(catchError(() => {
            return [{translation: m.moduleId, title: m.name, hasStrongs: false, license: null}] as any;
          }))
        )).subscribe(details => {
          const enriched = mods.map((m, i) => ({
            moduleId: m.moduleId,
            label: (details[i] as any)?.translation ?? m.moduleId,
            title: (details[i] as any)?.title ?? m.name,
          }));
          this.allModules.set(enriched);

          // Default: select active module + up to 2 others
          const active = this.nav.moduleId();
          const initial = new Set<string>();
          if (active) initial.add(active);
          for (const m of enriched) {
            if (initial.size >= 3) break;
            initial.add(m.moduleId);
          }
          this.selectedIds.set(initial);
          this.loadAllColumns();
        });
      }
    });
  }

  ngAfterViewInit(): void {
    // Set up synchronized scrolling after view is ready
    this.colBodies.changes.subscribe(() => this.bindSyncScroll());
    this.bindSyncScroll();
  }

  toggleModule(moduleId: string): void {
    const ids = new Set(this.selectedIds());
    if (ids.has(moduleId)) {
      if (ids.size <= 1) return; // keep at least one
      ids.delete(moduleId);
    } else {
      if (ids.size >= 4) return; // max 4 columns
      ids.add(moduleId);
    }
    this.selectedIds.set(ids);
    this.loadAllColumns();
  }

  isSelected(moduleId: string): boolean {
    return this.selectedIds().has(moduleId);
  }

  onVerseClick(verse: Verse): void {
    this.onVerseClickByNum(verse.verse);
  }

  onVerseClickByNum(verseNum: number): void {
    const already = this.nav.verse() === verseNum;
    const loc = this.nav.location();
    if (!loc) return;
    this.nav.navigate({ ...loc, verse: already ? null : verseNum });
  }

  isActiveVerse(verse: Verse): boolean {
    return this.nav.verse() === verse.verse;
  }

  private loadAllColumns(): void {
    const loc = this.nav.location();
    if (!loc) return;

    const ids = Array.from(this.selectedIds());
    const mods = this.allModules();

    // Build skeleton columns immediately
    const cols: CompareColumn[] = ids.map(id => ({
      moduleId: id,
      label: mods.find(m => m.moduleId === id)?.label ?? id,
      title: mods.find(m => m.moduleId === id)?.title ?? id,
      passage: null,
      loading: true,
      error: null,
    }));
    this.columns.set(cols);

    // Fetch all in parallel
    ids.forEach((id, i) => {
      this.bibleService.getChapter(id, loc.book, loc.chapter).pipe(
        catchError(() => EMPTY)
      ).subscribe({
        next: passage => {
          // Deduplicate verses
          const seen = new Set<number>();
          const verses = passage.verses.filter(v => {
            if (seen.has(v.verse)) return false;
            seen.add(v.verse);
            return true;
          });
          this.columns.update(c => {
            const updated = [...c];
            updated[i] = { ...updated[i], passage: { ...passage, verses }, loading: false };
            return updated;
          });
        },
        error: () => {
          this.columns.update(c => {
            const updated = [...c];
            updated[i] = { ...updated[i], loading: false, error: 'Failed to load' };
            return updated;
          });
        }
      });
    });
  }

  private bindSyncScroll(): void {
    const els = this.colBodies.toArray().map(r => r.nativeElement);
    els.forEach(el => {
      el.addEventListener('scroll', () => {
        if (!this.syncScroll() || this.isSyncing) return;
        this.isSyncing = true;
        els.forEach(other => {
          if (other !== el) other.scrollTop = el.scrollTop;
        });
        this.isSyncing = false;
      });
    });
  }

  trackByModule(_: number, col: CompareColumn): string { return col.moduleId; }
  trackByVerse(_: number, v: Verse): number { return v.verse; }
}
