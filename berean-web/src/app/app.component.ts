import { Component, inject } from "@angular/core";
import { DesktopShellComponent } from "./features/shell/desktop-shell.component";
import { TabletShellComponent } from "./features/shell/tablet-shell.component";
import { PhoneShellComponent } from "./features/shell/phone-shell.component";
import { KeyboardShortcutsService } from "./core/services/keyboard-shortcuts.service";
import { LayoutService } from "./core/services/layout.service";

/** Picks the shell for the current viewport (MOBILE_PLAN.md §2/§6). */
@Component({
  selector: "app-root",
  standalone: true,
  imports: [DesktopShellComponent, TabletShellComponent, PhoneShellComponent],
  template: `
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
  `,
})
export class AppComponent {
  private readonly _kb = inject(KeyboardShortcutsService); // activates global shortcuts
  readonly layout = inject(LayoutService);
}
