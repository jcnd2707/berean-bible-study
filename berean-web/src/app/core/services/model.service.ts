import { Injectable, inject, signal } from "@angular/core";
import { HttpClient } from "@angular/common/http";
import { environment } from "../../../environments/environment";

export interface ModelOption {
  id: string;
  name: string;
  isDefault: boolean;
  supportsTools: boolean;
}

@Injectable({ providedIn: "root" })
export class ModelService {
  private readonly http = inject(HttpClient);

  readonly models = signal<ModelOption[]>([]);
  readonly selectedModelId = signal<string>("");

  readonly ready: Promise<void>;

  constructor() {
    this.ready = new Promise((resolve) => {
      this.http
        .get<ModelOption[]>(`${environment.agentApiUrl}/api/models`)
        .subscribe({
          next: (models) => {
            this.models.set(models);
            const defaultModel = models.find((m) => m.isDefault) ?? models[0];
            this.selectedModelId.set(defaultModel?.id ?? "");
            resolve();
          },
          error: () => resolve(),
        });
    });
  }

  selectModel(id: string): void {
    this.selectedModelId.set(id);
  }
}
