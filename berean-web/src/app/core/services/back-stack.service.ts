import { Injectable, OnDestroy } from "@angular/core";

/**
 * Makes the Android back button close whatever overlay/sheet/drawer is on
 * screen instead of leaving the app (MOBILE_PLAN.md §4.4). Escape shares
 * `closeTopMost()` so both close the same thing the same way.
 *
 * Today's overlays are mutually exclusive (NavigationStateService only ever
 * has one of showSearch/showCompare/showNotesList/showBooks true at a time),
 * so this tracks a single active closer rather than a full stack. Switching
 * from one overlay straight to another calls `open()` again, which replaces
 * the current history entry in place — it does not pop-then-push, which
 * would race `history.back()`'s asynchronous navigation against the
 * synchronous `pushState` of whatever opens next.
 */
@Injectable({ providedIn: "root" })
export class BackStackService implements OnDestroy {
  private active: (() => void) | null = null;
  private readonly onPopState = () => {
    const closeFn = this.active;
    this.active = null;
    closeFn?.();
  };

  constructor() {
    window.addEventListener("popstate", this.onPopState);
  }

  ngOnDestroy(): void {
    window.removeEventListener("popstate", this.onPopState);
  }

  /** Registers the overlay/sheet/drawer that just opened. `close` must be idempotent. */
  open(close: () => void): void {
    if (this.active) {
      history.replaceState({ bereanOverlay: true }, "");
    } else {
      history.pushState({ bereanOverlay: true }, "");
    }
    this.active = close;
  }

  /**
   * Call when the registered overlay closes some way other than the back
   * button (a visible ✕, Escape, selecting a result). No-ops if `closeFn`
   * isn't the currently-active one (e.g. it already lost a race to another
   * open()), so call sites can call this unconditionally on close.
   */
  close(closeFn: () => void): void {
    if (this.active !== closeFn) return;
    this.active = null;
    history.back();
  }

  /** Closes whatever's currently registered. Returns false if nothing was open. */
  closeTopMost(): boolean {
    const closeFn = this.active;
    if (!closeFn) return false;
    this.close(closeFn);
    closeFn();
    return true;
  }
}
