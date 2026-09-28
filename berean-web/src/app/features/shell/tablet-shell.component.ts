import {
  Component,
  ElementRef,
  ViewChild,
  OnDestroy,
  effect,
  inject,
  signal,
} from "@angular/core";
import { ToolbarComponent } from "./toolbar.component";
import { BookSidebarComponent } from "../book-sidebar/book-sidebar.component";
import { BibleReaderComponent } from "../bible-reader/bible-reader.component";
import { RightPanelComponent } from "../right-panel/right-panel.component";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { LayoutService, Orientation } from "../../core/services/layout.service";

/**
 * Tablet shell (MOBILE_PLAN.md §3, Tablet — the priority device): reader
 * plus a tabbed study panel (Commentary/Notes/Xrefs/Dictionary/Ask, see
 * RightPanelComponent), stacked in portrait and side-by-side in landscape,
 * with book/chapter navigation moved into a slide-in drawer since there's
 * no room for BookSidebar's own column. Picked by AppComponent when
 * `LayoutService.layout() === 'tablet'`.
 */
@Component({
  selector: "app-tablet-shell",
  standalone: true,
  imports: [
    ToolbarComponent,
    BookSidebarComponent,
    BibleReaderComponent,
    RightPanelComponent,
  ],
  template: `
    <div class="tablet-shell">
      <app-toolbar />

      <div
        class="body"
        [class.landscape]="layout.orientation() === 'landscape'"
        #body
      >
        <main class="reader-pane">
          <app-bible-reader />
        </main>

        <div
          class="handle"
          (pointerdown)="onHandlePointerDown($event)"
          [class.collapsed]="collapsed()"
        >
          <div class="handle-grip"></div>
        </div>

        <aside
          class="study-pane"
          [class.collapsed]="collapsed()"
          [style.flexBasis.%]="collapsed() ? null : studyShare() * 100"
        >
          <app-right-panel />
        </aside>
      </div>

      <!-- Book/chapter drawer (desktop shows BookSidebar as an inline column instead) -->
      @if (nav.showBookDrawer()) {
        <div class="drawer-backdrop" (click)="nav.closeBookDrawer()"></div>
        <aside class="book-drawer">
          <app-book-sidebar />
        </aside>
      }
    </div>
  `,
  styles: [
    `
      .tablet-shell {
        display: flex;
        flex-direction: column;
        height: 100vh;
        height: 100dvh;
        overflow: hidden;
        background: #0b1520;
        color: #e8e3d8;
        font-family: "Segoe UI", system-ui, sans-serif;
        position: relative;
      }
      .body {
        display: flex;
        flex-direction: column;
        flex: 1;
        overflow: hidden;
        &.landscape {
          flex-direction: row;
        }
      }
      .reader-pane {
        flex: 1 1 auto;
        min-height: 120px;
        min-width: 200px;
        overflow: hidden;
        display: flex;
        flex-direction: column;
      }
      .study-pane {
        flex: 0 0 50%;
        min-height: 120px;
        overflow: hidden;
        display: flex;
        flex-direction: column;
        transition: flex-basis 0.05s linear;
        &.collapsed {
          flex-basis: 44px !important;
          min-height: 44px;
        }
      }
      .landscape .study-pane {
        min-width: 280px;
        &.collapsed {
          flex-basis: 0 !important;
          min-width: 0;
        }
      }
      .handle {
        flex-shrink: 0;
        height: 20px;
        cursor: row-resize;
        touch-action: none;
        display: flex;
        align-items: center;
        justify-content: center;
        background: rgba(255, 255, 255, 0.03);
      }
      .landscape .handle {
        height: auto;
        width: 20px;
        cursor: col-resize;
      }
      .handle-grip {
        width: 40px;
        height: 3px;
        border-radius: 2px;
        background: rgba(255, 255, 255, 0.18);
      }
      .landscape .handle-grip {
        width: 3px;
        height: 40px;
      }
      .drawer-backdrop {
        position: absolute;
        inset: 0;
        background: rgba(0, 0, 0, 0.4);
        z-index: 20;
      }
      .book-drawer {
        position: absolute;
        top: 0;
        bottom: 0;
        left: 0;
        width: min(320px, 85vw);
        background: #0d1a26;
        z-index: 21;
        overflow: hidden;
        display: flex;
        flex-direction: column;
        box-shadow: 4px 0 20px rgba(0, 0, 0, 0.35);
      }
    `,
  ],
})
export class TabletShellComponent implements OnDestroy {
  @ViewChild("body") bodyRef!: ElementRef<HTMLElement>;
  readonly layout = inject(LayoutService);
  readonly nav = inject(NavigationStateService);

  private readonly SHARE_MIN = 0.2;
  private readonly SHARE_MAX = 0.8;
  private readonly DEFAULTS: Record<Orientation, number> = {
    portrait: 0.5,
    landscape: 0.4,
  };

  readonly studyShare = signal<number>(this.DEFAULTS.portrait);
  readonly collapsed = signal(false);

  private dragging = false;
  private dragStartPos = 0;
  private dragStartShare = 0;
  private dragOrientation: Orientation = "portrait";
  private lastPointerUpAt = 0;

  private readonly pointermove = (e: PointerEvent) => this.onPointerMove(e);
  private readonly pointerup = (e: PointerEvent) => this.onPointerUp(e);

  private readonly _orientationLoad = effect(() => {
    const o = this.layout.orientation();
    this.studyShare.set(this.loadShare(o));
  });

  // Selecting a book/chapter from the drawer should return focus to the
  // reader instead of leaving the drawer open over it.
  private lastChapterKey: string | null = null;
  private readonly _closeDrawerOnNavigate = effect(() => {
    const loc = this.nav.location();
    const key = loc ? `${loc.moduleId}|${loc.book}|${loc.chapter}` : null;
    if (
      this.lastChapterKey !== null &&
      key !== this.lastChapterKey &&
      this.nav.showBookDrawer()
    ) {
      this.nav.closeBookDrawer();
    }
    this.lastChapterKey = key;
  });

  // A chat citation or the verse action bar's jump buttons need the study
  // panel actually visible, not just its active tab changed underneath a
  // collapsed panel.
  private readonly _uncollapseOnTabRequest = effect(() => {
    if (this.nav.requestedRightTab()) this.collapsed.set(false);
  });

  constructor() {
    window.addEventListener("pointermove", this.pointermove);
    window.addEventListener("pointerup", this.pointerup);
    window.addEventListener("pointercancel", this.pointerup);
  }

  ngOnDestroy(): void {
    window.removeEventListener("pointermove", this.pointermove);
    window.removeEventListener("pointerup", this.pointerup);
    window.removeEventListener("pointercancel", this.pointerup);
  }

  onHandlePointerDown(e: PointerEvent): void {
    // A quick second tap on the handle collapses/restores the study panel
    // instead of dragging — lets you read full-screen without losing your
    // split (MOBILE_PLAN.md §3).
    const now = Date.now();
    if (now - this.lastPointerUpAt < 350) {
      this.collapsed.update((v) => !v);
      return;
    }
    this.dragOrientation = this.layout.orientation();
    this.dragging = true;
    this.dragStartPos =
      this.dragOrientation === "landscape" ? e.clientX : e.clientY;
    this.dragStartShare = this.studyShare();
    this.collapsed.set(false);
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    e.preventDefault();
  }

  private onPointerMove(e: PointerEvent): void {
    if (!this.dragging) return;
    const el = this.bodyRef.nativeElement;
    const landscape = this.dragOrientation === "landscape";
    const total = landscape ? el.clientWidth : el.clientHeight;
    if (total <= 0) return;
    const pos = landscape ? e.clientX : e.clientY;
    const delta = (pos - this.dragStartPos) / total;
    // Dragging the handle toward the far edge (down in portrait, right in
    // landscape) grows the reader and shrinks the study panel.
    const next = this.dragStartShare - delta;
    this.studyShare.set(
      Math.max(this.SHARE_MIN, Math.min(this.SHARE_MAX, next)),
    );
  }

  private onPointerUp(e: PointerEvent): void {
    if (this.dragging) {
      this.saveShare(this.dragOrientation, this.studyShare());
    }
    this.dragging = false;
    this.lastPointerUpAt = Date.now();
  }

  private shareKey(o: Orientation): string {
    return `berean_tabletStudyShare_${o}`;
  }

  private loadShare(o: Orientation): number {
    try {
      const stored = localStorage.getItem(this.shareKey(o));
      const n = stored ? parseFloat(stored) : NaN;
      if (isNaN(n)) return this.DEFAULTS[o];
      return Math.max(this.SHARE_MIN, Math.min(this.SHARE_MAX, n));
    } catch {
      return this.DEFAULTS[o];
    }
  }

  private saveShare(o: Orientation, value: number): void {
    try {
      localStorage.setItem(this.shareKey(o), String(value));
    } catch {}
  }
}
