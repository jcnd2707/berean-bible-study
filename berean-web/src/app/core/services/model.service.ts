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
  private readonly STORAGE_KEY = "berean_bible_model";
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
            const stored = localStorage.getItem(this.STORAGE_KEY);
            const found = models.find((m) => m.id === stored);
            const defaultModel = models.find((m) => m.isDefault) ?? models[0];
            this.selectedModelId.set((found ?? defaultModel)?.id ?? "");
            resolve();
          },
          error: () => resolve(),
        });
    });
  }

  selectModel(id: string): void {
    this.selectedModelId.set(id);
    localStorage.setItem(this.STORAGE_KEY, id);
  }
}
