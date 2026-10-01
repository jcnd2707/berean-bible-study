import { Component, inject, signal } from "@angular/core";
import { DesktopShellComponent } from "./features/shell/desktop-shell.component";
import { TabletShellComponent } from "./features/shell/tablet-shell.component";
import { PhoneShellComponent } from "./features/shell/phone-shell.component";
import { KeyboardShortcutsService } from "./core/services/keyboard-shortcuts.service";
import { ReadingPositionService } from "./core/services/reading-position.service";
import { ReadingProgressService } from "./core/services/reading-progress.service";
import { LayoutService } from "./core/services/layout.service";
import { ProfileService } from "./core/services/profile.service";
import { ProfilePickerComponent } from "./features/profiles/profile-picker.component";
import { NavigationStateService } from "./core/services/navigation-state.service";

/** Picks the shell for the current viewport (MOBILE_PLAN.md §2/§6), behind the profile picker (D1). */
@Component({
  selector: "app-root",
  standalone: true,
  imports: [DesktopShellComponent, TabletShellComponent, PhoneShellComponent, ProfilePickerComponent],
  template: `
    @if (!profileReady()) {
      <!-- Waiting on ProfileService.ready — avoids flashing the picker before localStorage is checked. -->
    } @else if (!profiles.current()) {
      <app-profile-picker />
    } @else {
      @switch (layout.layout()) {
        @case ("tablet") {
          <app-tablet-shell />
        }
        @case ("phone") {
          <app-phone-shell />
        }
        @default {
          <app-desktop-shell />
        }
      }

      @if (nav.showProfileSwitcher()) {
        <app-profile-picker [closable]="true" (closed)="nav.closeProfileSwitcher()" />
      }
    }
  `,
})
export class AppComponent {
  private readonly _kb = inject(KeyboardShortcutsService); // activates global shortcuts
  private readonly _readingPosition = inject(ReadingPositionService); // activates save/restore
  private readonly _readingProgress = inject(ReadingProgressService); // loads which chapters are marked read
  readonly layout = inject(LayoutService);
  readonly profiles = inject(ProfileService);
  readonly nav = inject(NavigationStateService);
  readonly profileReady = signal(false);

  constructor() {
    this.profiles.ready.then(() => this.profileReady.set(true));
  }
}
