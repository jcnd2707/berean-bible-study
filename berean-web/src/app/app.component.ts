import {
  Component,
  ElementRef,
  ViewChild,
  AfterViewInit,
  OnDestroy,
  signal,
  inject,
} from "@angular/core";
import { TitleBarComponent } from "./features/shell/title-bar.component";
import { ToolbarComponent } from "./features/shell/toolbar.component";
import { BookSidebarComponent } from "./features/book-sidebar/book-sidebar.component";
import { BibleReaderComponent } from "./features/bible-reader/bible-reader.component";
import { CommentaryComponent } from "./features/commentary/commentary.component";

@Component({
  selector: "app-root",
  standalone: true,
  imports: [
    TitleBarComponent,
    ToolbarComponent,
    BookSidebarComponent,
    BibleReaderComponent,
    CommentaryComponent,
  ],
  template: `
    <div class="app-shell">
      <app-title-bar />
      <app-toolbar />

      <div class="main-area" #mainArea>
        <!-- Left sidebar: fixed width -->
        <aside class="sidebar-col">
          <app-book-sidebar />
        </aside>

        <!-- Divider: sidebar | center -->
        <div
          class="divider divider-v"
          #dividerLeft
          (mousedown)="startDragLeft($event)"
        >
          <div class="divider-handle"></div>
        </div>

        <!-- Center: bible reader -->
        <main class="center-col" [style.width.px]="centerWidth()">
          <app-bible-reader />
        </main>

        <!-- Divider: center | right -->
        <div
          class="divider divider-v"
          #dividerRight
          (mousedown)="startDragRight($event)"
        >
          <div class="divider-handle"></div>
        </div>

        <!-- Right column -->
        <aside class="right-col" [style.width.px]="rightWidth()">
          <!-- Commentary panel (top, resizable height) -->
          <div class="right-top" [style.height.px]="commentaryHeight()">
            <app-commentary />
          </div>

          <!-- Divider: commentary | ai chat -->
          <div
            class="divider divider-h"
            (mousedown)="startDragVertical($event)"
          >
            <div class="divider-handle-h"></div>
          </div>

          <!-- AI Chat placeholder (bottom, remainder) -->
          <div class="right-bottom">
            <div class="ai-placeholder">
              <span>AI Chat · Phase 4</span>
            </div>
          </div>
        </aside>
      </div>
    </div>
  `,
  styles: [
    `
      .app-shell {
        display: flex;
        flex-direction: column;
        height: 100vh;
        overflow: hidden;
        background: #0b1520;
        color: #e8e3d8;
        font-family: "Segoe UI", system-ui, sans-serif;
        font-size: 12px;
      }

      .main-area {
        display: flex;
        flex: 1;
        overflow: hidden;
      }

      .sidebar-col {
        width: 138px;
        min-width: 138px;
        flex-shrink: 0;
        overflow: hidden;
        display: flex;
        flex-direction: column;
      }

      .center-col {
        min-width: 200px;
        overflow: hidden;
        display: flex;
        flex-direction: column;
        flex-shrink: 0;
      }

      .right-col {
        min-width: 180px;
        overflow: hidden;
        display: flex;
        flex-direction: column;
        flex-shrink: 0;
      }

      .right-top {
        min-height: 80px;
        overflow: hidden;
        flex-shrink: 0;
      }

      .right-bottom {
        flex: 1;
        overflow: hidden;
        min-height: 80px;
      }

      /* Vertical dividers (between columns) */
      .divider-v {
        width: 5px;
        cursor: col-resize;
        flex-shrink: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        background: rgba(255, 255, 255, 0.03);
        transition: background 0.15s;
        z-index: 10;

        &:hover,
        &.dragging {
          background: rgba(200, 146, 42, 0.18);
        }
      }

      .divider-handle {
        width: 1px;
        height: 40px;
        border-radius: 1px;
        background: rgba(255, 255, 255, 0.12);
      }

      /* Horizontal divider (within right column) */
      .divider-h {
        height: 5px;
        cursor: row-resize;
        flex-shrink: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        background: rgba(255, 255, 255, 0.03);
        transition: background 0.15s;

        &:hover,
        &.dragging {
          background: rgba(200, 146, 42, 0.18);
        }
      }

      .divider-handle-h {
        height: 1px;
        width: 40px;
        border-radius: 1px;
        background: rgba(255, 255, 255, 0.12);
      }

      .ai-placeholder {
        height: 100%;
        display: flex;
        align-items: center;
        justify-content: center;
        background: #0e0c26;
        color: rgba(255, 255, 255, 0.25);
        font-size: 11px;
        font-style: italic;
      }
    `,
  ],
})
export class AppComponent implements AfterViewInit, OnDestroy {
  @ViewChild("mainArea") mainAreaRef!: ElementRef<HTMLElement>;

  readonly centerWidth = signal<number>(0);
  readonly rightWidth = signal<number>(318);
  readonly commentaryHeight = signal<number>(0);

  private readonly SIDEBAR_W = 138;
  private readonly DIVIDER_W = 5; // two vertical dividers
  private readonly MIN_CENTER = 200;
  private readonly MIN_RIGHT = 180;
  private readonly MIN_COMMENTARY = 80;
  private readonly MIN_CHAT = 80;

  private dragging: "left" | "right" | "vertical" | null = null;
  private startX = 0;
  private startY = 0;
  private startCenter = 0;
  private startRight = 0;
  private startCommentary = 0;
  private activeDivider: HTMLElement | null = null;

  private readonly mousemove = (e: MouseEvent) => this.onMouseMove(e);
  private readonly mouseup = () => this.onMouseUp();

  ngAfterViewInit(): void {
    this.initSizes();
    window.addEventListener("resize", () => this.initSizes());
    window.addEventListener("mousemove", this.mousemove);
    window.addEventListener("mouseup", this.mouseup);
  }

  ngOnDestroy(): void {
    window.removeEventListener("mousemove", this.mousemove);
    window.removeEventListener("mouseup", this.mouseup);
  }

  private initSizes(): void {
    const total = this.mainAreaRef.nativeElement.clientWidth;
    const available = total - this.SIDEBAR_W - this.DIVIDER_W * 2;
    const right = Math.max(
      this.MIN_RIGHT,
      Math.min(this.rightWidth(), available - this.MIN_CENTER),
    );
    const center = available - right;
    this.centerWidth.set(center);
    this.rightWidth.set(right);

    const rightH = this.mainAreaRef.nativeElement.clientHeight;
    this.commentaryHeight.set(Math.max(this.MIN_COMMENTARY, rightH - 295 - 5));
  }

  startDragLeft(e: MouseEvent): void {
    // Left divider: not used for resizing (sidebar is fixed), kept for future
    e.preventDefault();
  }

  startDragRight(e: MouseEvent): void {
    this.dragging = "right";
    this.startX = e.clientX;
    this.startCenter = this.centerWidth();
    this.startRight = this.rightWidth();
    this.activeDivider = (e.target as HTMLElement).closest(
      ".divider",
    ) as HTMLElement;
    this.activeDivider?.classList.add("dragging");
    e.preventDefault();
  }

  startDragVertical(e: MouseEvent): void {
    this.dragging = "vertical";
    this.startY = e.clientY;
    this.startCommentary = this.commentaryHeight();
    this.activeDivider = (e.target as HTMLElement).closest(
      ".divider",
    ) as HTMLElement;
    this.activeDivider?.classList.add("dragging");
    e.preventDefault();
  }

  private onMouseMove(e: MouseEvent): void {
    if (!this.dragging) return;

    if (this.dragging === "right") {
      const dx = e.clientX - this.startX;
      const total = this.mainAreaRef.nativeElement.clientWidth;
      const available = total - this.SIDEBAR_W - this.DIVIDER_W * 2;
      const newCenter = Math.max(
        this.MIN_CENTER,
        Math.min(this.startCenter + dx, available - this.MIN_RIGHT),
      );
      const newRight = available - newCenter;
      this.centerWidth.set(newCenter);
      this.rightWidth.set(newRight);
    }

    if (this.dragging === "vertical") {
      const dy = e.clientY - this.startY;
      const totalH = this.mainAreaRef.nativeElement.clientHeight;
      const maxComm = totalH - this.MIN_CHAT - 5;
      const newComm = Math.max(
        this.MIN_COMMENTARY,
        Math.min(this.startCommentary + dy, maxComm),
      );
      this.commentaryHeight.set(newComm);
    }
  }

  private onMouseUp(): void {
    this.dragging = null;
    this.activeDivider?.classList.remove("dragging");
    this.activeDivider = null;
  }
}
