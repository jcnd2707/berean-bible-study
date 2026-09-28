import { Injectable, inject, signal } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { Observable, firstValueFrom } from "rxjs";
import { environment } from "../../../environments/environment";
import { AgentHubService } from "./agent-hub.service";
import { PendingSavesService } from "./pending-saves.service";

export interface Profile {
  id: string;
  name: string;
  color: string | null;
  createdAt: string;
}

const STORAGE_KEY = "berean_profileId";

/**
 * "Who's studying?" profiles — a name picker, not a login (see PROFILES_AND_SESSIONS_PLAN.md D1).
 * Every browser remembers the chosen profile in localStorage; the interceptor and the hub URL read
 * it from there rather than from this service, so it works before this service has even loaded.
 */
@Injectable({ providedIn: "root" })
export class ProfileService {
  private readonly http = inject(HttpClient);
  private readonly hub = inject(AgentHubService);
  private readonly pendingSaves = inject(PendingSavesService);
  private readonly base = environment.apiBaseUrl;

  readonly current = signal<Profile | null>(null);
  readonly profiles = signal<Profile[]>([]);

  /** Resolves once the stored profile id (if any) has been checked against the server. */
  readonly ready: Promise<void>;

  constructor() {
    this.ready = this.init();
  }

  private async init(): Promise<void> {
    try {
      this.profiles.set(await firstValueFrom(this.list()));
    } catch {
      // Resource API not reachable yet — the picker's own call will retry.
    }

    let storedId: string | null = null;
    try {
      storedId = localStorage.getItem(STORAGE_KEY);
    } catch {
      /* private browsing, storage disabled, etc. */
    }
    if (!storedId) return;

    try {
      const profile = await firstValueFrom(this.http.get<Profile>(`${this.base}/api/profiles/${storedId}`));
      this.current.set(profile);
    } catch {
      // 404 — the stored id no longer exists. Clear it so the picker shows instead of looping.
      try {
        localStorage.removeItem(STORAGE_KEY);
      } catch {}
    }
  }

  list(): Observable<Profile[]> {
    return this.http.get<Profile[]>(`${this.base}/api/profiles`);
  }

  async create(name: string): Promise<Profile> {
    const profile = await firstValueFrom(this.http.post<Profile>(`${this.base}/api/profiles`, { name }));
    this.profiles.update((list) => [...list, profile]);
    return profile;
  }

  async rename(id: string, name: string): Promise<Profile> {
    const profile = await firstValueFrom(this.http.put<Profile>(`${this.base}/api/profiles/${id}`, { name }));
    this.profiles.update((list) => list.map((p) => (p.id === id ? profile : p)));
    if (this.current()?.id === id) this.current.set(profile);
    return profile;
  }

  unownedNotesCount(): Observable<{ notes: number }> {
    return this.http.get<{ notes: number }>(`${this.base}/api/profiles/unowned`);
  }

  unownedSessionsCount(): Observable<{ count: number }> {
    return this.http.get<{ count: number }>(`${environment.agentApiUrl}/api/conversations/unowned`);
  }

  adoptUnownedNotes(id: string): Observable<{ moved: number; remaining: number }> {
    return this.http.post<{ moved: number; remaining: number }>(`${this.base}/api/profiles/${id}/adopt-unowned`, {});
  }

  adoptUnownedSessions(id: string): Observable<{ moved: number; remaining: number }> {
    return this.http.post<{ moved: number; remaining: number }>(
      `${environment.agentApiUrl}/api/profiles/${id}/adopt-unowned-conversations`,
      {},
    );
  }

  /** The picker's first-ever choice: nothing has loaded under any profile yet, so no reload is needed. */
  select(profile: Profile): void {
    this.setStoredId(profile.id);
    this.current.set(profile);
  }

  /**
   * Switches to a different profile on an already-running app (D4): refuses while an answer is
   * streaming, flushes any pending save under the *old* profile and waits for it — flushing after
   * changing the stored id would save it into the new profile instead — then reloads so every
   * service (hub connection, chat messages, notes panel, noted-verse markers) starts clean.
   */
  async switchTo(profile: Profile): Promise<{ ok: true } | { ok: false; reason: string }> {
    if (this.hub.isAnswering()) return { ok: false, reason: "Wait for the answer to finish." };

    await this.pendingSaves.flushAll();
    this.setStoredId(profile.id);
    location.reload();
    return { ok: true };
  }

  private setStoredId(id: string): void {
    try {
      localStorage.setItem(STORAGE_KEY, id);
    } catch {}
  }
}
