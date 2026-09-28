import { Component, inject } from "@angular/core";
import { DesktopShellComponent } from "./features/shell/desktop-shell.component";
import { TabletShellComponent } from "./features/shell/tablet-shell.component";
import { KeyboardShortcutsService } from "./core/services/keyboard-shortcuts.service";
import { LayoutService } from "./core/services/layout.service";

/**
 * Picks the shell for the current viewport (MOBILE_PLAN.md §2/§6). Phone
 * doesn't have its own shell yet (phase 3) — it falls back to the desktop
 * shell for now, same as before phase 2, rather than the tablet shell,
 * since the tablet shell's fixed reader/study split hasn't been designed
 * for a screen too narrow to show both usefully at once.
 */
@Component({
  selector: "app-root",
  standalone: true,
  imports: [DesktopShellComponent, TabletShellComponent],
  template: `
    @switch (layout.layout()) {
      @case ("tablet") {
        <app-tablet-shell />
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
