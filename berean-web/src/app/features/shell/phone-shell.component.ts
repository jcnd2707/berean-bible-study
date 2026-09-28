import { Component, computed, effect, inject, signal } from "@angular/core";
import { BibleReaderComponent } from "../bible-reader/bible-reader.component";
import { RightPanelComponent } from "../right-panel/right-panel.component";
import { AiChatComponent } from "../ai-chat/ai-chat.component";
import { DictionaryPanelComponent } from "../dictionary/dictionary-panel.component";
import { BookSidebarComponent } from "../book-sidebar/book-sidebar.component";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { WordSelectionService } from "../../core/services/word-selection.service";
import { BackStackService } from "../../core/services/back-stack.service";
import { PreferencesService } from "../../core/services/preferences.service";
import { LayoutService } from "../../core/services/layout.service";

type PhoneView = "read" | "study" | "ask" | "more";

/**
 * Phone shell (MOBILE_PLAN.md §3, Phone): one view at a time behind a
 * bottom nav, since there's no room to show the reader alongside anything
 * else at 411px wide. Mostly reuses what the tablet shell (phase 2)
 * already built — same BibleReader/RightPanel/AiChat/BookSidebar
 * components, just one visible at a time instead of two side by side.
 * Picked by AppComponent when `LayoutService.layout() === 'phone'`.
 *
 * Not implemented here (deferred, see MOBILE_PLAN.md §3/§5 and phase 5):
 * - The verse action bar (Ask AI/Commentary/Notes/Xrefs/Look up word)
 *   that the phone mockup shows under a selected verse — same reason as
 *   phase 1: it needs a defined "jump to X" target, which only exists now
 *   that this shell does, but building it is its own pass, not bundled in
 *   here.
 * - The dictionary sheet's peek/half/full drag gesture — it's a plain
 *   slide-up sheet with a close button, no drag-to-resize.
 * - Hiding the top bar on scroll-down / revealing on scroll-up in the
 *   short landscape case — the bottom-nav-becomes-a-side-rail part is
 *   implemented (pure CSS), the scroll-tracking part is not.
 * - Back-button support for bottom-nav tab switches (only the dictionary
 *   sheet and book picker are wired to BackStackService, matching phase
 *   1's overlay set) — see back-stack.service.ts for why this only
 *   tracks one active overlay, not a real nested stack.
 */
@Component({
  selector: "app-phone-shell",
  standalone: true,
  imports: [
    BibleReaderComponent,
    RightPanelComponent,
    AiChatComponent,
    DictionaryPanelComponent,
    BookSidebarComponent,
  ],
  template: `
    <div class="phone-shell">
      <div class="phone-topbar">
        <button
          class="tb-icon"
          [disabled]="!canGoPrev()"
          (click)="nav.prevChapter()"
          title="Previous chapter"
        >
          ◀
        </button>
        <button class="tb-ref" (click)="nav.toggleBookDrawer()">
          {{ referenceLabel() }}
        </button>
        <button
          class="tb-icon"
          [disabled]="!canGoNext()"
          (click)="nav.nextChapter()"
          title="Next chapter"
        >
          ▶
        </button>
      </div>

      <div class="views">
        <!-- Kept mounted (not @if) so switching tabs can't drop an
             in-progress note or interrupt an in-flight chat answer. -->
        <div class="view" [hidden]="activeView() !== 'read'">
          <app-bible-reader />
        </div>
        <div class="view" [hidden]="activeView() !== 'study'">
          <app-right-panel />
        </div>
        <div class="view" [hidden]="activeView() !== 'ask'">
          <app-ai-chat />
        </div>
        @if (activeView() === "more") {
          <div class="view more-view">
            <button class="more-item" (click)="nav.toggleSearch()">
              🔍 Search
            </button>
            <button class="more-item" (click)="nav.toggleCompare()">
              ⇄ Compare
            </button>
            <button class="more-item" (click)="nav.toggleNotesList()">
              📝 My Notes
            </button>
            <button class="more-item" (click)="nav.toggleBooks()">
              📚 Books
            </button>
            <div class="more-row">
              <button (click)="prefs.decreaseFontSize()">A−</button>
              <span>{{ prefs.fontSize() }}px</span>
              <button (click)="prefs.increaseFontSize()">A+</button>
              <button (click)="prefs.toggleTheme()">
                {{ prefs.readerTheme() === "light" ? "☽ Dark" : "☀ Light" }}
              </button>
            </div>
          </div>
        }
      </div>

      <!-- Dictionary bottom sheet: opened by touch word lookup (§4.3/§4.5) -->
      @if (showDictSheet()) {
        <div class="sheet-backdrop" (click)="closeDictSheet()"></div>
        <div class="dict-sheet">
          <div class="sheet-handle-row">
            <div class="sheet-handle"></div>
            <button class="sheet-close" (click)="closeDictSheet()">✕</button>
          </div>
          <app-dictionary-panel />
        </div>
      }

      <!-- Book/chapter picker -->
      @if (nav.showBookDrawer()) {
        <div class="picker-sheet">
          <div class="picker-header">
            <span>Books &amp; Chapters</span>
            <button class="sheet-close" (click)="nav.closeBookDrawer()">
              ✕
            </button>
          </div>
          <app-book-sidebar />
        </div>
      }

      <nav class="bottom-nav">
        <button
          class="nav-btn"
          [class.active]="activeView() === 'read'"
          (click)="activeView.set('read')"
        >
          <span class="nav-icon">📖</span><span class="nav-label">Read</span>
        </button>
        <button
          class="nav-btn"
          [class.active]="activeView() === 'study'"
          (click)="activeView.set('study')"
        >
          <span class="nav-icon">📝</span><span class="nav-label">Study</span>
        </button>
        <button
          class="nav-btn"
          [class.active]="activeView() === 'ask'"
          (click)="activeView.set('ask')"
        >
          <span class="nav-icon">💬</span><span class="nav-label">Ask</span>
        </button>
        <button
          class="nav-btn"
          [class.active]="activeView() === 'more'"
          (click)="activeView.set('more')"
        >
          <span class="nav-icon">⋯</span><span class="nav-label">More</span>
        </button>
      </nav>
    </div>
  `,
  styles: [
    `
      .phone-shell {
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
      .phone-topbar {
        display: flex;
        align-items: center;
        gap: var(--gap);
        min-height: 56px;
        padding: 6px 12px;
        background: #0d1a26;
        border-bottom: 1px solid rgba(255, 255, 255, 0.05);
        flex-shrink: 0;
      }
      .tb-icon {
        min-width: var(--tap-min);
        min-height: var(--tap-min);
        background: rgba(255, 255, 255, 0.05);
        border: 0.5px solid rgba(255, 255, 255, 0.1);
        border-radius: 4px;
        color: rgba(255, 255, 255, 0.7);
        font-size: var(--fs-md);
        cursor: pointer;
        &:disabled {
          opacity: 0.3;
        }
      }
      .tb-ref {
        flex: 1;
        min-height: var(--tap-min);
        background: rgba(255, 255, 255, 0.06);
        border: 0.5px solid rgba(255, 255, 255, 0.14);
        border-radius: 6px;
        color: #e8e3d8;
        font-size: var(--fs-md);
        font-weight: 600;
        cursor: pointer;
      }
      .views {
        flex: 1;
        overflow: hidden;
        display: flex;
        flex-direction: column;
        position: relative;
      }
      .view {
        flex: 1;
        overflow: hidden;
        display: flex;
        flex-direction: column;
      }
      .more-view {
        overflow-y: auto;
        padding: 16px;
        gap: 10px;
        background: #faf8f3;
        color: #2c2825;
      }
      .more-item {
        display: block;
        width: 100%;
        text-align: left;
        padding: var(--pad-btn);
        min-height: var(--tap-min);
        margin-bottom: 8px;
        background: #fff;
        border: 1px solid #ddd8ce;
        border-radius: 8px;
        font-size: var(--fs-md);
        font-family: inherit;
        cursor: pointer;
      }
      .more-row {
        display: flex;
        align-items: center;
        gap: var(--gap);
        margin-top: 12px;
        button {
          min-width: var(--tap-min);
          min-height: var(--tap-min);
          background: #fff;
          border: 1px solid #ddd8ce;
          border-radius: 8px;
          font-size: var(--fs-md);
          font-family: inherit;
          cursor: pointer;
        }
      }
      .bottom-nav {
        display: flex;
        flex-shrink: 0;
        background: #0d1a26;
        border-top: 1px solid rgba(255, 255, 255, 0.05);
        padding-bottom: env(safe-area-inset-bottom);
      }
      .nav-btn {
        flex: 1;
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 2px;
        min-height: var(--tap-min);
        padding: 6px 0;
        background: transparent;
        border: none;
        color: rgba(255, 255, 255, 0.45);
        cursor: pointer;
        &.active {
          color: #c8922a;
        }
      }
      .nav-icon {
        font-size: var(--fs-lg);
        line-height: 1;
      }
      .nav-label {
        font-size: var(--fs-2xs);
      }
      .sheet-backdrop {
        position: absolute;
        inset: 0;
        background: rgba(0, 0, 0, 0.4);
        z-index: 30;
      }
      .dict-sheet {
        position: absolute;
        left: 0;
        right: 0;
        bottom: 0;
        max-height: 70vh;
        background: #f5f2eb;
        border-radius: 14px 14px 0 0;
        z-index: 31;
        display: flex;
        flex-direction: column;
        overflow: hidden;
        box-shadow: 0 -4px 20px rgba(0, 0, 0, 0.3);
      }
      .sheet-handle-row {
        display: flex;
        align-items: center;
        justify-content: center;
        position: relative;
        padding: 8px;
        flex-shrink: 0;
      }
      .sheet-handle {
        width: 36px;
        height: 4px;
        border-radius: 2px;
        background: rgba(0, 0, 0, 0.15);
      }
      .sheet-close {
        position: absolute;
        right: 8px;
        top: 4px;
        width: var(--tap-min);
        height: var(--tap-min);
        background: transparent;
        border: none;
        color: #a09890;
        cursor: pointer;
      }
      .picker-sheet {
        position: absolute;
        inset: 0;
        background: #0d1a26;
        z-index: 30;
        display: flex;
        flex-direction: column;
        overflow: hidden;
      }
      .picker-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 10px 16px;
        min-height: 56px;
        font-size: var(--fs-md);
        font-weight: 600;
        color: #e8e3d8;
        border-bottom: 1px solid rgba(255, 255, 255, 0.08);
        flex-shrink: 0;
      }

      /* Too short for a top bar + a full-height view + a bottom bar
         (MOBILE_PLAN.md §3, phone landscape). The scroll-hide behavior for
         .phone-topbar described in the plan isn't implemented — this only
         gets the bottom nav out of the way. */
      @media (max-height: 450px) {
        .phone-shell {
          flex-direction: row;
        }
        .phone-topbar {
          display: none;
        }
        .bottom-nav {
          flex-direction: column;
          width: 56px;
          border-top: none;
          border-right: 1px solid rgba(255, 255, 255, 0.05);
          padding-bottom: 0;
          padding-left: env(safe-area-inset-left);
        }
        .views {
          order: 2;
        }
      }
    `,
  ],
})
export class PhoneShellComponent {
  readonly nav = inject(NavigationStateService);
  readonly prefs = inject(PreferencesService);
  readonly layout = inject(LayoutService);
  private readonly wordSelection = inject(WordSelectionService);
  private readonly backStack = inject(BackStackService);

  readonly activeView = signal<PhoneView>("read");
  readonly showDictSheet = signal(false);

  readonly canGoPrev = computed(() => (this.nav.chapter() ?? 1) > 1);
  readonly canGoNext = computed(
    () => (this.nav.chapter() ?? 1) < this.nav.maxChapter(),
  );
  readonly referenceLabel = computed(() => {
    const loc = this.nav.location();
    if (!loc) return "No passage selected";
    const verse = loc.verse ? `:${loc.verse}` : "";
    return `${loc.book} ${loc.chapter}${verse}`;
  });

  private readonly closeDictSheetFn = () => this.closeDictSheet();

  // §4.5: cross-panel requests must switch views since only one is visible.
  private readonly _wordLookupOpensSheet = effect(() => {
    if (this.wordSelection.selection() && !this.showDictSheet()) {
      this.showDictSheet.set(true);
      this.backStack.open(this.closeDictSheetFn);
    }
  });
  private readonly _citationSwitchesToStudy = effect(() => {
    if (this.nav.requestedRightTab()) this.activeView.set("study");
  });
  private readonly _overlaySwitchesToRead = effect(() => {
    if (
      this.nav.showSearch() ||
      this.nav.showCompare() ||
      this.nav.showNotesList() ||
      this.nav.showBooks()
    ) {
      this.activeView.set("read");
    }
  });

  closeDictSheet(): void {
    if (!this.showDictSheet()) return;
    this.showDictSheet.set(false);
    this.backStack.close(this.closeDictSheetFn);
  }
}
