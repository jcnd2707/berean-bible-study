import { Component, inject } from "@angular/core";
import { ProfileService } from "../../core/services/profile.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";

@Component({
  selector: "app-title-bar",
  standalone: true,
  template: `
    <div class="title-bar">
      <div class="logo">
        <span class="logo-name">Berean</span>
        <span class="logo-sub">Bible Study</span>
      </div>
      <nav class="menu-row">
        <span class="menu-item">Bible</span>
        <span class="menu-item">Commentary</span>
        <span class="menu-item">Dictionary</span>
        <span class="menu-item">Tools</span>
        <span class="menu-item">Options</span>
        <span class="menu-item">Window</span>
      </nav>
      @if (profiles.current(); as p) {
        <button class="profile-chip" (click)="nav.toggleProfileSwitcher()" title="Switch person">
          {{ p.name }}
        </button>
      }
      <div class="wm-btns">
        <div class="wm wm-r"></div>
        <div class="wm wm-y"></div>
        <div class="wm wm-g"></div>
      </div>
    </div>
  `,
  styles: [
    `
      .title-bar {
        display: flex;
        align-items: center;
        height: 38px;
        background: #070e18;
        padding: 0 16px;
        gap: 20px;
        border-bottom: 1px solid rgba(255, 255, 255, 0.05);
        flex-shrink: 0;
        user-select: none;
      }
      .logo {
        display: flex;
        align-items: center;
        gap: 8px;
        flex-shrink: 0;
      }
      .logo-name {
        font-family: Georgia, serif;
        font-size: 16px;
        font-weight: bold;
        color: #c8922a;
        letter-spacing: 1px;
      }
      .logo-sub {
        font-size: 9px;
        font-weight: 400;
        color: rgba(200, 146, 42, 0.55);
        letter-spacing: 2px;
        text-transform: uppercase;
      }
      .menu-row {
        display: flex;
        gap: 18px;
      }
      .menu-item {
        font-size: 10px;
        color: rgba(255, 255, 255, 0.38);
        cursor: default;
        padding: 2px 0;
        transition: color 0.1s;
      }
      .menu-item:hover {
        color: rgba(255, 255, 255, 0.75);
      }
      .profile-chip {
        margin-left: auto;
        background: rgba(200, 146, 42, 0.12);
        border: 1px solid rgba(200, 146, 42, 0.35);
        border-radius: 12px;
        padding: 3px 12px;
        font-size: 10px;
        color: #c8922a;
        cursor: pointer;
        font-family: inherit;
      }
      .profile-chip:hover {
        background: rgba(200, 146, 42, 0.2);
      }
      .wm-btns {
        margin-left: 12px;
        display: flex;
        gap: 8px;
      }
      .wm {
        width: 10px;
        height: 10px;
        border-radius: 50%;
      }
      .wm-r {
        background: #ff5f57;
      }
      .wm-y {
        background: #febc2e;
      }
      .wm-g {
        background: #28c840;
      }
    `,
  ],
})
export class TitleBarComponent {
  readonly profiles = inject(ProfileService);
  readonly nav = inject(NavigationStateService);
}
