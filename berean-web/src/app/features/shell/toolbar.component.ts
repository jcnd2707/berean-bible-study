import { Component, inject, computed } from "@angular/core";
import { CommonModule } from "@angular/common";
import { NavigationStateService } from "../../core/services/navigation-state.service";

@Component({
  selector: "app-toolbar",
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="toolbar">
      <button
        class="tb-btn"
        (click)="prevChapter()"
        [disabled]="!canGoPrev()"
        title="Previous chapter"
      >
        ◀
      </button>
      <button class="tb-btn" (click)="nextChapter()" title="Next chapter">
        ▶
      </button>

      <div class="tb-sep"></div>

      <div class="ref-box">{{ referenceLabel() }}</div>

      <div class="tb-sep"></div>

      <button class="tb-btn">Compare</button>
      <button class="tb-btn">Parallel</button>
      <button class="tb-btn">Search</button>

      <div class="tb-sep"></div>

      <button class="tb-btn">Strong's</button>
      <button class="tb-btn">Interlinear</button>

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
      .tb-sep {
        width: 0.5px;
        height: 20px;
        background: rgba(255, 255, 255, 0.08);
        margin: 0 4px;
        flex-shrink: 0;
      }
      .ref-box {
        background: rgba(255, 255, 255, 0.06);
        border: 0.5px solid rgba(255, 255, 255, 0.14);
        border-radius: 5px;
        padding: 4px 12px;
        color: #e8e3d8;
        font-size: 12px;
        min-width: 150px;
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
  private readonly nav = inject(NavigationStateService);

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

  prevChapter(): void {
    this.nav.prevChapter();
  }
  nextChapter(): void {
    this.nav.nextChapter();
  }
}
