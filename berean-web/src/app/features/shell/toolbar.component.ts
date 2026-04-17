import { Component, inject, computed, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { PreferencesService } from "../../core/services/preferences.service";

@Component({
  selector: "app-toolbar",
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="toolbar">
      <button
        class="tb-btn"
        (click)="nav.prevChapter()"
        [disabled]="!canGoPrev()"
        title="Previous chapter (Alt+←)"
      >
        ◀
      </button>
      <button
        class="tb-btn"
        (click)="nav.nextChapter()"
        title="Next chapter (Alt+→)"
      >
        ▶
      </button>

      <div class="tb-sep"></div>

      <form class="ref-form" (submit)="onRefSubmit($event)">
        <input
          class="ref-box"
          [value]="editing() ? refInput() : referenceLabel()"
          (focus)="onRefFocus()"
          (blur)="onRefBlur()"
          (input)="refInput.set($any($event.target).value)"
          (keydown.escape)="onRefEscape()"
          placeholder="e.g. John 3:16"
          title="Type a reference and press Enter to navigate"
        />
      </form>

      <div class="tb-sep"></div>

      <button
        class="tb-btn"
        [class.tb-btn--active]="nav.showCompare()"
        (click)="nav.toggleCompare()"
        title="Compare translations"
      >
        Compare
      </button>
      <button
        class="tb-btn"
        [class.tb-btn--active]="nav.showSearch()"
        (click)="nav.toggleSearch()"
        title="Search the Bible (Ctrl+F)"
      >
        Search
      </button>
      <button
        class="tb-btn"
        [class.tb-btn--active]="nav.showNotesList()"
        (click)="nav.toggleNotesList()"
        title="Browse all notes"
      >
        My Notes
      </button>

      <div class="tb-sep"></div>

      <button
        class="tb-btn tb-btn--icon"
        (click)="prefs.decreaseFontSize()"
        title="Decrease font size"
      >
        A−
      </button>
      <button
        class="tb-btn tb-btn--icon"
        (click)="prefs.increaseFontSize()"
        title="Increase font size"
      >
        A+
      </button>
      <button
        class="tb-btn tb-btn--icon"
        [title]="
          prefs.readerTheme() === 'light'
            ? 'Switch to dark reader'
            : 'Switch to light reader'
        "
        (click)="prefs.toggleTheme()"
      >
        {{ prefs.readerTheme() === "light" ? "☽" : "☀" }}
      </button>

      <div class="toolbar-right">
        <span class="ai-ctx-badge">{{ contextLabel() }}</span>
      </div>
    </div>
  `,
  styles: [
    `
      .toolbar {
        display: flex;
        align-items: center;
        height: 36px;
        background: #0d1a26;
        padding: 0 12px;
        gap: 6px;
        border-bottom: 1px solid rgba(255, 255, 255, 0.05);
        flex-shrink: 0;
      }
      .tb-btn {
        background: rgba(255, 255, 255, 0.05);
        border: 0.5px solid rgba(255, 255, 255, 0.1);
        border-radius: 4px;
        padding: 3px 9px;
        color: rgba(255, 255, 255, 0.45);
        font-size: 10px;
        cursor: pointer;
        letter-spacing: 0.2px;
        transition:
          background 0.1s,
          color 0.1s;
        font-family: inherit;
      }
      .tb-btn:hover:not(:disabled) {
        background: rgba(255, 255, 255, 0.09);
        color: rgba(255, 255, 255, 0.75);
      }
      .tb-btn:disabled {
        opacity: 0.3;
        cursor: default;
      }
      .tb-btn--active {
        background: rgba(200, 146, 42, 0.15);
        border-color: rgba(200, 146, 42, 0.4);
        color: #c8922a;
      }
      .tb-btn--icon {
        padding: 3px 7px;
        font-size: 11px;
      }
      .tb-sep {
        width: 0.5px;
        height: 20px;
        background: rgba(255, 255, 255, 0.08);
        margin: 0 4px;
        flex-shrink: 0;
      }
      .ref-form {
        display: flex;
      }
      .ref-box {
        background: rgba(255, 255, 255, 0.06);
        border: 0.5px solid rgba(255, 255, 255, 0.14);
        border-radius: 5px;
        padding: 4px 12px;
        color: #e8e3d8;
        font-size: 12px;
        min-width: 150px;
        outline: none;
        font-family: inherit;
        transition:
          border-color 0.15s,
          background 0.15s;
        cursor: pointer;
      }
      .ref-box:focus {
        border-color: rgba(200, 146, 42, 0.5);
        background: rgba(255, 255, 255, 0.09);
        cursor: text;
      }
      .ref-box::placeholder {
        color: rgba(255, 255, 255, 0.25);
      }
      .toolbar-right {
        margin-left: auto;
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .ai-ctx-badge {
        font-size: 9px;
        padding: 3px 10px;
        border-radius: 10px;
        background: rgba(200, 146, 42, 0.1);
        border: 0.5px solid rgba(200, 146, 42, 0.3);
        color: #c8922a;
        letter-spacing: 0.3px;
        white-space: nowrap;
      }
    `,
  ],
})
export class ToolbarComponent {
  readonly nav = inject(NavigationStateService);
  readonly prefs = inject(PreferencesService);

  readonly editing = signal(false);
  readonly refInput = signal("");

  readonly referenceLabel = computed(() => {
    const loc = this.nav.location();
    if (!loc) return "No passage selected";
    const verse = loc.verse ? `:${loc.verse}` : "";
    return `${loc.book} ${loc.chapter}${verse}`;
  });

  readonly contextLabel = computed(() => {
    const loc = this.nav.location();
    if (!loc) return "No context";
    const verse = loc.verse ? `:${loc.verse}` : "";
    return `Context: ${loc.book} ${loc.chapter}${verse} · ${loc.moduleId}`;
  });

  readonly canGoPrev = computed(() => {
    const loc = this.nav.location();
    return loc !== null && loc.chapter > 1;
  });

  onRefFocus(): void {
    this.editing.set(true);
    this.refInput.set(this.referenceLabel());
  }

  onRefBlur(): void {
    this.editing.set(false);
  }

  onRefEscape(): void {
    this.editing.set(false);
    (document.activeElement as HTMLElement)?.blur();
  }

  onRefSubmit(e: Event): void {
    e.preventDefault();
    const parsed = this.parseReference(this.refInput().trim());
    if (!parsed) return;
    const loc = this.nav.location();
    if (!loc) return;

    const bookAbbr = this.nav.bookAbbrFromName(parsed.book);
    if (!bookAbbr) return;

    this.nav.navigate({
      moduleId: loc.moduleId,
      book: bookAbbr,
      chapter: parsed.chapter,
      verse: parsed.verse ?? null,
    });
    this.editing.set(false);
    (document.activeElement as HTMLElement)?.blur();
  }

  /**
   * Parses "John 3:16", "Gen 1", "Genesis 1:1", "Jhn 3.16" etc.
   */
  private parseReference(
    input: string,
  ): { book: string; chapter: number; verse?: number } | null {
    // Match: word(s) + space + number + optional :or. + number
    const match = input.match(/^(.+?)\s+(\d+)(?:[:.]\s*(\d+))?$/);
    if (!match) return null;
    return {
      book: match[1].trim(),
      chapter: parseInt(match[2], 10),
      verse: match[3] ? parseInt(match[3], 10) : undefined,
    };
  }
}
