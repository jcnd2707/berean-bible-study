import { Injectable, signal } from "@angular/core";

export type ReaderTheme = "light" | "dark";

@Injectable({ providedIn: "root" })
export class PreferencesService {
  private readonly FONT_KEY = "berean_fontSize";
  private readonly THEME_KEY = "berean_readerTheme";

  readonly fontSize = signal<number>(this.loadFont());
  readonly readerTheme = signal<ReaderTheme>(this.loadTheme());

  increaseFontSize(): void {
    const next = Math.min(this.fontSize() + 1, 22);
    this.fontSize.set(next);
    this.save(this.FONT_KEY, String(next));
  }

  decreaseFontSize(): void {
    const next = Math.max(this.fontSize() - 1, 11);
    this.fontSize.set(next);
    this.save(this.FONT_KEY, String(next));
  }

  toggleTheme(): void {
    const next: ReaderTheme = this.readerTheme() === "light" ? "dark" : "light";
    this.readerTheme.set(next);
    this.save(this.THEME_KEY, next);
  }

  private save(key: string, value: string): void {
    try {
      localStorage.setItem(key, value);
    } catch {}
  }

  private loadFont(): number {
    try {
      const stored = localStorage.getItem(this.FONT_KEY);
      const n = stored ? parseInt(stored, 10) : 13;
      return isNaN(n) ? 13 : Math.max(11, Math.min(22, n));
    } catch {
      return 13;
    }
  }

  private loadTheme(): ReaderTheme {
    try {
      const stored = localStorage.getItem(this.THEME_KEY);
      return stored === "dark" ? "dark" : "light";
    } catch {
      return "light";
    }
  }
}
