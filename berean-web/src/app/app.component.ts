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
import { RightPanelComponent } from "./features/right-panel/right-panel.component";
import { KeyboardShortcutsService } from "./core/services/keyboard-shortcuts.service";
import { AiChatComponent } from "./features/ai-chat/ai-chat.component";
import { DictionaryPanelComponent } from "./features/dictionary/dictionary-panel.component";

@Component({
  selector: "app-root",
  standalone: true,
  imports: [
    TitleBarComponent,
    ToolbarComponent,
    BookSidebarComponent,
    BibleReaderComponent,
    RightPanelComponent,
    AiChatComponent,
    DictionaryPanelComponent,
  ],
  template: `
    <div class="app-shell">
      <app-title-bar />
      <app-toolbar />

      <div class="main-area" #mainArea>
        <aside class="sidebar-col">
          <app-book-sidebar />
        </aside>

        <div class="divider divider-v" (mousedown)="startDragLeft($event)">
          <div class="divider-handle"></div>
        </div>

        <!-- Center column: reader (top) + divider + dictionary (bottom) -->
        <main class="center-col" [style.width.px]="centerWidth()">
          <div class="center-top" [style.height.px]="readerHeight()">
            <app-bible-reader />
          </div>

          <div class="divider divider-h" (mousedown)="startDragCenterV($event)">
            <div class="divider-handle-h"></div>
          </div>

          <div class="center-bottom">
            <app-dictionary-panel />
          </div>
        </main>

        <div class="divider divider-v" (mousedown)="startDragRight($event)">
          <div class="divider-handle"></div>
        </div>

        <aside class="right-col" [style.width.px]="rightWidth()">
          <div class="right-top" [style.height.px]="commentaryHeight()">
            <app-right-panel />
          </div>

          <div class="divider divider-h" (mousedown)="startDragRightV($event)">
            <div class="divider-handle-h"></div>
          </div>

          <div class="right-bottom">
            <app-ai-chat />
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
      .center-top {
        min-height: 100px;
        overflow: hidden;
        flex-shrink: 0;
      }
      .center-bottom {
        flex: 1;
        min-height: 80px;
        overflow: hidden;
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
    `,
  ],
})
export class AppComponent implements AfterViewInit, OnDestroy {
  @ViewChild("mainArea") mainAreaRef!: ElementRef<HTMLElement>;
  private readonly _kb = inject(KeyboardShortcutsService); // activates global shortcuts

  readonly centerWidth = signal<number>(0);
  readonly rightWidth = signal<number>(318);
  readonly readerHeight = signal<number>(0);
  readonly commentaryHeight = signal<number>(0);

  private readonly SIDEBAR_W = 138;
  private readonly DIVIDER_W = 5;
  private readonly MIN_CENTER = 200;
  private readonly MIN_RIGHT = 180;
  private readonly MIN_READER = 100;
  private readonly MIN_DICT = 80;
  private readonly MIN_COMM = 80;
  private readonly MIN_CHAT = 80;
  private readonly DICT_DEFAULT = 175;
  private readonly CHAT_DEFAULT = 295;

  private dragging: "right" | "centerV" | "rightV" | null = null;
  private startX = 0;
  private startY = 0;
  private startCenter = 0;
  private startRight = 0;
  private startReader = 0;
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
    const el = this.mainAreaRef.nativeElement;
    const totalW = el.clientWidth;
    const totalH = el.clientHeight;

    // Horizontal: sidebar + 2 dividers + center + right
    const availW = totalW - this.SIDEBAR_W - this.DIVIDER_W * 2;
    const right = Math.max(
      this.MIN_RIGHT,
      Math.min(this.rightWidth(), availW - this.MIN_CENTER),
    );
    const center = availW - right;
    this.centerWidth.set(center);
    this.rightWidth.set(right);

    // Center vertical: reader + dict
    const reader = Math.max(
      this.MIN_READER,
      totalH - this.DICT_DEFAULT - this.DIVIDER_W,
    );
    this.readerHeight.set(reader);

    // Right vertical: commentary + chat
    const comm = Math.max(
      this.MIN_COMM,
      totalH - this.CHAT_DEFAULT - this.DIVIDER_W,
    );
    this.commentaryHeight.set(comm);
  }

  startDragLeft(e: MouseEvent): void {
    e.preventDefault();
  }

  startDragRight(e: MouseEvent): void {
    this.dragging = "right";
    this.startX = e.clientX;
    this.startCenter = this.centerWidth();
    this.startRight = this.rightWidth();
    this.setDragging(e);
  }

  startDragCenterV(e: MouseEvent): void {
    this.dragging = "centerV";
    this.startY = e.clientY;
    this.startReader = this.readerHeight();
    this.setDragging(e);
  }

  startDragRightV(e: MouseEvent): void {
    this.dragging = "rightV";
    this.startY = e.clientY;
    this.startCommentary = this.commentaryHeight();
    this.setDragging(e);
  }

  private setDragging(e: MouseEvent): void {
    this.activeDivider = (e.target as HTMLElement).closest(
      ".divider",
    ) as HTMLElement;
    this.activeDivider?.classList.add("dragging");
    e.preventDefault();
  }

  private onMouseMove(e: MouseEvent): void {
    if (!this.dragging) return;
    const el = this.mainAreaRef.nativeElement;

    if (this.dragging === "right") {
      const dx = e.clientX - this.startX;
      const availW = el.clientWidth - this.SIDEBAR_W - this.DIVIDER_W * 2;
      const newC = Math.max(
        this.MIN_CENTER,
        Math.min(this.startCenter + dx, availW - this.MIN_RIGHT),
      );
      this.centerWidth.set(newC);
      this.rightWidth.set(availW - newC);
    }

    if (this.dragging === "centerV") {
      const dy = e.clientY - this.startY;
      const totalH = el.clientHeight;
      const maxR = totalH - this.MIN_DICT - this.DIVIDER_W;
      const newR = Math.max(
        this.MIN_READER,
        Math.min(this.startReader + dy, maxR),
      );
      this.readerHeight.set(newR);
    }

    if (this.dragging === "rightV") {
      const dy = e.clientY - this.startY;
      const totalH = el.clientHeight;
      const maxC = totalH - this.MIN_CHAT - this.DIVIDER_W;
      const newC = Math.max(
        this.MIN_COMM,
        Math.min(this.startCommentary + dy, maxC),
      );
      this.commentaryHeight.set(newC);
    }
  }

  private onMouseUp(): void {
    this.dragging = null;
    this.activeDivider?.classList.remove("dragging");
    this.activeDivider = null;
  }
}
