import { Component, effect, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { CommentaryComponent } from "../commentary/commentary.component";
import { NotesComponent } from "../notes/notes.component";
import { CrossReferencesComponent } from "../cross-references/cross-references.component";
import { DictionaryPanelComponent } from "../dictionary/dictionary-panel.component";
import { AiChatComponent } from "../ai-chat/ai-chat.component";
import {
  NavigationStateService,
  RightTab,
} from "../../core/services/navigation-state.service";
import { WordSelectionService } from "../../core/services/word-selection.service";
import { LayoutService } from "../../core/services/layout.service";

@Component({
  selector: "app-right-panel",
  standalone: true,
  imports: [
    CommonModule,
    CommentaryComponent,
    NotesComponent,
    CrossReferencesComponent,
    DictionaryPanelComponent,
    AiChatComponent,
  ],
  template: `
    <div class="right-panel">
      <div class="panel-tab-strip">
        <button
          class="panel-tab"
          [class.active]="activeTab() === 'commentary'"
          (click)="activeTab.set('commentary')"
        >
          Commentary
        </button>
        <button
          class="panel-tab"
          [class.active]="activeTab() === 'notes'"
          (click)="activeTab.set('notes')"
        >
          Notes
        </button>
        <button
          class="panel-tab"
          [class.active]="activeTab() === 'xrefs'"
          (click)="activeTab.set('xrefs')"
        >
          Cross-refs
        </button>
        <!--
          Desktop already shows the dictionary and chat as their own
          always-visible panels elsewhere, so they'd be redundant tabs
          here. Tablet/phone have no other home for them, so this panel
          becomes their tabbed "study panel" (MOBILE_PLAN.md §3/§5).
        -->
        @if (layout.layout() !== "desktop") {
          <button
            class="panel-tab"
            [class.active]="activeTab() === 'dictionary'"
            (click)="activeTab.set('dictionary')"
          >
            Dictionary
          </button>
          <button
            class="panel-tab"
            [class.active]="activeTab() === 'ask'"
            (click)="activeTab.set('ask')"
          >
            Ask
          </button>
        }
      </div>
      <div class="panel-content">
        <!--
          Kept mounted and hidden via [hidden], not @if — switching tabs
          used to destroy/recreate these, which silently dropped an
          in-progress note save that hadn't hit its debounce yet
          (MOBILE_PLAN.md §1, "one important catch").
        -->
        <app-commentary [hidden]="activeTab() !== 'commentary'" />
        <app-notes [hidden]="activeTab() !== 'notes'" />
        <app-cross-references [hidden]="activeTab() !== 'xrefs'" />
        @if (layout.layout() !== "desktop") {
          <app-dictionary-panel [hidden]="activeTab() !== 'dictionary'" />
          <app-ai-chat [hidden]="activeTab() !== 'ask'" />
        }
      </div>
    </div>
  `,
  styles: [
    `
      :host {
        display: flex;
        flex-direction: column;
        height: 100%;
        overflow: hidden;
      }
      .right-panel {
        display: flex;
        flex-direction: column;
        height: 100%;
        overflow: hidden;
      }
      .panel-tab-strip {
        display: flex;
        background: #eae6de;
        border-bottom: 0.5px solid #ddd8ce;
        flex-shrink: 0;
        overflow-x: auto;
        scrollbar-width: none;
        &::-webkit-scrollbar {
          display: none;
        }
      }
      .panel-tab {
        padding: 6px 12px;
        min-height: var(--tap-min);
        font-size: var(--fs-sm);
        font-weight: 600;
        color: #a09890;
        background: transparent;
        border: none;
        border-bottom: 2px solid transparent;
        cursor: pointer;
        letter-spacing: 0.2px;
        white-space: nowrap;
        transition:
          color 0.1s,
          border-color 0.1s;
        font-family: inherit;
        &:hover {
          color: #5c5650;
        }
        &.active {
          color: #1c1917;
          border-bottom-color: #c8922a;
        }
      }
      .panel-content {
        flex: 1;
        overflow: hidden;
        display: flex;
        flex-direction: column;
      }
      /*
       * Each child sets its own :host { display: flex }, which as an author
       * rule beats the UA [hidden] rule regardless of specificity — so
       * [hidden] alone wouldn't hide them. This overrides it explicitly.
       */
      app-commentary[hidden],
      app-notes[hidden],
      app-cross-references[hidden],
      app-dictionary-panel[hidden],
      app-ai-chat[hidden] {
        display: none;
      }
    `,
  ],
})
export class RightPanelComponent {
  private readonly nav = inject(NavigationStateService);
  private readonly wordSelection = inject(WordSelectionService);
  readonly layout = inject(LayoutService);

  readonly activeTab = signal<RightTab>("commentary");

  // A chat citation can ask for a particular tab.
  private readonly _tabRequest = effect(() => {
    const tab = this.nav.requestedRightTab();
    if (!tab) return;
    this.activeTab.set(tab);
    this.nav.clearRequestedRightTab();
  });

  // MOBILE_PLAN.md §4.5: looking up a word should switch to the Dictionary
  // tab where there's no separate always-visible dictionary panel to show it in.
  private readonly _wordLookupSwitch = effect(() => {
    const selection = this.wordSelection.selection();
    if (!selection) return;
    if (this.layout.layout() === "desktop") return;
    this.activeTab.set("dictionary");
  });
}
