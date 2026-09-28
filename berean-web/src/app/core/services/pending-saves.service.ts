import { Injectable } from "@angular/core";

type FlushFn = () => Promise<void>;

/**
 * Components with an unsaved, debounced edit (NotesComponent's 1s auto-save) register a flush
 * callback here, so switching profiles can save it under the *old* profile before the page
 * reloads — see PROFILES_AND_SESSIONS_PLAN.md D4. A flush that fails is swallowed: the switch
 * must not get stuck because one save failed.
 */
@Injectable({ providedIn: "root" })
export class PendingSavesService {
  private readonly flushers = new Set<FlushFn>();

  /** Returns an unregister function — call it (e.g. from ngOnDestroy) once the component is gone. */
  register(flush: FlushFn): () => void {
    this.flushers.add(flush);
    return () => this.flushers.delete(flush);
  }

  async flushAll(): Promise<void> {
    await Promise.all([...this.flushers].map((f) => f().catch(() => {})));
  }
}
