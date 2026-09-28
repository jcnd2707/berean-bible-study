import { Injectable, inject, OnDestroy } from "@angular/core";
import { NavigationStateService } from "./navigation-state.service";
import { BackStackService } from "./back-stack.service";

@Injectable({ providedIn: "root" })
export class KeyboardShortcutsService implements OnDestroy {
  private readonly nav = inject(NavigationStateService);
  private readonly backStack = inject(BackStackService);
  private readonly handler = (e: KeyboardEvent) => this.onKeydown(e);

  constructor() {
    window.addEventListener("keydown", this.handler);
  }

  ngOnDestroy(): void {
    window.removeEventListener("keydown", this.handler);
  }

  private onKeydown(e: KeyboardEvent): void {
    const tag = (e.target as HTMLElement).tagName.toLowerCase();
    const isInput = tag === "input" || tag === "textarea" || tag === "select";

    // Escape — close the top-most open overlay, sharing the same
    // "close top-most" logic as the Android back button (MOBILE_PLAN.md §4.4).
    if (e.key === "Escape") {
      if (this.backStack.closeTopMost()) {
        e.preventDefault();
        return;
      }
      if (this.nav.verse()) {
        this.nav.clearVerse();
        e.preventDefault();
        return;
      }
      return;
    }

    // Don't fire navigation shortcuts when typing in an input
    if (isInput) return;

    // Ctrl+F / Cmd+F — open search
    if ((e.ctrlKey || e.metaKey) && e.key === "f") {
      e.preventDefault();
      this.nav.toggleSearch();
      return;
    }

    // Alt+← / Alt+→ — prev/next chapter
    if (e.altKey && e.key === "ArrowLeft") {
      e.preventDefault();
      this.nav.prevChapter();
      return;
    }
    if (e.altKey && e.key === "ArrowRight") {
      e.preventDefault();
      this.nav.nextChapter();
      return;
    }
  }
}
