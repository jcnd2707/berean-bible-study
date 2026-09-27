import { Injectable, inject, signal } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { environment } from "../../../environments/environment";

export interface PerspectiveOption {
  id: string;
  tradition: string;
  label: string;
  citationPrefix: string;
  topK: number;
}

/**
 * Perspectives configured for this deployment (empty on the public default config — see
 * "Perspectives" in Berean.Agent.Api/appsettings.json). One is selected per conversation and
 * locked once it starts; a different perspective means a new conversation.
 */
@Injectable({ providedIn: "root" })
export class PerspectiveService {
  private readonly http = inject(HttpClient);

  readonly perspectives = signal<PerspectiveOption[]>([]);
  readonly selectedId = signal<string | null>(null);

  readonly ready: Promise<void>;

  constructor() {
    this.ready = new Promise((resolve) => {
      this.http
        .get<PerspectiveOption[]>(`${environment.agentApiUrl}/api/perspectives`)
        .subscribe({
          next: (list) => {
            this.perspectives.set(list);
            resolve();
          },
          error: () => resolve(),
        });
    });
  }

  select(id: string | null): void {
    this.selectedId.set(id);
  }

  /** What StartConversation expects: empty when none is selected. */
  selectedIds(): string[] {
    const id = this.selectedId();
    return id ? [id] : [];
  }
}
