import { Component, EventEmitter, Input, Output, computed, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { Profile, ProfileService } from "../../core/services/profile.service";

/**
 * "Who's studying?" — the profile picker (PROFILES_AND_SESSIONS_PLAN.md D1/Phase 1). Used two
 * ways: as AppComponent's full-screen gate before any profile is chosen (closable=false), and as
 * a dismissible overlay to switch profiles later (closable=true), opened from the title bar or the
 * phone shell's More menu.
 */
@Component({
  selector: "app-profile-picker",
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="picker" [class.picker--overlay]="closable">
      <div class="picker-card">
        @if (closable) {
          <button class="close-btn" (click)="closed.emit()" title="Cancel">✕</button>
        }
        <h1>Who's studying?</h1>

        @if (profiles().length > 0) {
          <div class="tiles">
            @for (p of profiles(); track p.id) {
              <button
                class="tile"
                [class.tile--current]="p.id === current()?.id"
                [disabled]="switching()"
                (click)="choose(p)"
              >
                <span class="avatar" [style.background]="p.color || '#c8922a'">{{ initial(p) }}</span>
                <span class="name">{{ p.name }}</span>
              </button>
            }
          </div>
        }

        @if (!adding()) {
          <button class="add-btn" (click)="startAdding()">+ Add person</button>
        } @else {
          <form class="add-form" (submit)="submit($event)">
            <input
              class="name-input"
              [(ngModel)]="newName"
              name="name"
              placeholder="Name"
              autocomplete="off"
            />

            @if (showAdoptOption()) {
              <label class="adopt">
                <input type="checkbox" [(ngModel)]="keepExisting" name="keepExisting" />
                Keep the notes and study sessions made before profiles?
                @if (unownedNotes() !== null && unownedSessions() !== null) {
                  ({{ unownedNotes() }} note{{ unownedNotes() === 1 ? "" : "s" }},
                  {{ unownedSessions() }} session{{ unownedSessions() === 1 ? "" : "s" }})
                }
              </label>
            }

            <div class="add-actions">
              <button type="submit" [disabled]="!newName.trim() || creating()">Create</button>
              <button type="button" (click)="adding.set(false)">Cancel</button>
            </div>
          </form>
        }

        @if (error()) {
          <p class="error">{{ error() }}</p>
        }
      </div>
    </div>
  `,
  styles: [
    `
      .picker {
        position: fixed;
        inset: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        background: #0b1520;
        z-index: 100;
      }
      .picker--overlay {
        background: rgba(0, 0, 0, 0.6);
      }
      .picker-card {
        position: relative;
        width: min(420px, 90vw);
        background: #101c29;
        border: 1px solid rgba(255, 255, 255, 0.08);
        border-radius: 14px;
        padding: 32px 28px;
        color: #e8e3d8;
        font-family: "Segoe UI", system-ui, sans-serif;
      }
      .close-btn {
        position: absolute;
        top: 12px;
        right: 12px;
        width: 32px;
        height: 32px;
        background: transparent;
        border: none;
        color: rgba(255, 255, 255, 0.5);
        font-size: 16px;
        cursor: pointer;
      }
      h1 {
        margin: 0 0 20px;
        font-family: Georgia, serif;
        font-size: 22px;
        text-align: center;
        color: #c8922a;
      }
      .tiles {
        display: flex;
        flex-wrap: wrap;
        gap: 14px;
        justify-content: center;
        margin-bottom: 18px;
      }
      .tile {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 8px;
        width: 92px;
        background: transparent;
        border: 1px solid transparent;
        border-radius: 10px;
        padding: 10px 4px;
        cursor: pointer;
        color: inherit;
        font-family: inherit;
      }
      .tile:hover:not(:disabled) {
        background: rgba(255, 255, 255, 0.05);
      }
      .tile--current {
        border-color: rgba(200, 146, 42, 0.5);
      }
      .tile:disabled {
        opacity: 0.5;
        cursor: default;
      }
      .avatar {
        width: 52px;
        height: 52px;
        border-radius: 50%;
        display: flex;
        align-items: center;
        justify-content: center;
        font-size: 20px;
        font-weight: 600;
        color: #0b1520;
      }
      .name {
        font-size: 13px;
        text-align: center;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        max-width: 100%;
      }
      .add-btn {
        display: block;
        width: 100%;
        padding: 10px;
        background: rgba(255, 255, 255, 0.05);
        border: 1px dashed rgba(255, 255, 255, 0.2);
        border-radius: 8px;
        color: rgba(255, 255, 255, 0.7);
        cursor: pointer;
        font-family: inherit;
      }
      .add-form {
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .name-input {
        padding: 10px 12px;
        background: rgba(255, 255, 255, 0.06);
        border: 1px solid rgba(255, 255, 255, 0.14);
        border-radius: 6px;
        color: #e8e3d8;
        font-size: 14px;
        font-family: inherit;
      }
      .adopt {
        display: flex;
        align-items: flex-start;
        gap: 8px;
        font-size: 12px;
        color: rgba(255, 255, 255, 0.65);
      }
      .add-actions {
        display: flex;
        gap: 10px;
      }
      .add-actions button {
        flex: 1;
        padding: 8px;
        border-radius: 6px;
        cursor: pointer;
        font-family: inherit;
      }
      .add-actions button[type="submit"] {
        background: #c8922a;
        border: none;
        color: #0b1520;
        font-weight: 600;
      }
      .add-actions button[type="submit"]:disabled {
        opacity: 0.5;
        cursor: default;
      }
      .add-actions button[type="button"] {
        background: transparent;
        border: 1px solid rgba(255, 255, 255, 0.2);
        color: rgba(255, 255, 255, 0.7);
      }
      .error {
        margin-top: 12px;
        color: #e06c6c;
        font-size: 13px;
        text-align: center;
      }
    `,
  ],
})
export class ProfilePickerComponent {
  private readonly profileService = inject(ProfileService);

  /** When true, this is the "switch profile" overlay rather than the initial full-screen gate. */
  @Input() closable = false;
  @Output() closed = new EventEmitter<void>();

  readonly profiles = this.profileService.profiles;
  readonly current = this.profileService.current;

  readonly adding = signal(false);
  readonly creating = signal(false);
  readonly switching = signal(false);
  readonly error = signal<string | null>(null);
  readonly unownedNotes = signal<number | null>(null);
  readonly unownedSessions = signal<number | null>(null);

  newName = "";
  keepExisting = true;

  readonly showAdoptOption = computed(() => this.profiles().length === 0);

  startAdding(): void {
    this.error.set(null);
    this.adding.set(true);
    if (this.showAdoptOption() && this.unownedNotes() === null) {
      this.profileService.unownedNotesCount().subscribe({
        next: (r) => this.unownedNotes.set(r.notes),
        error: () => this.unownedNotes.set(null),
      });
      this.profileService.unownedSessionsCount().subscribe({
        next: (r) => this.unownedSessions.set(r.count),
        error: () => this.unownedSessions.set(null),
      });
    }
  }

  initial(p: Profile): string {
    return p.name.trim().charAt(0).toUpperCase() || "?";
  }

  async choose(p: Profile): Promise<void> {
    if (!this.closable) {
      this.profileService.select(p);
      return;
    }
    if (p.id === this.current()?.id) {
      this.closed.emit();
      return;
    }
    this.switching.set(true);
    this.error.set(null);
    const result = await this.profileService.switchTo(p);
    if (!result.ok) {
      this.error.set(result.reason);
      this.switching.set(false);
    }
    // On success the page reloads — nothing left to update here.
  }

  async submit(e: Event): Promise<void> {
    e.preventDefault();
    const name = this.newName.trim();
    if (!name) return;

    const isFirstProfile = this.showAdoptOption();
    this.creating.set(true);
    this.error.set(null);
    try {
      const profile = await this.profileService.create(name);
      if (isFirstProfile && this.keepExisting) {
        await Promise.all([
          new Promise<void>((resolve) =>
            this.profileService.adoptUnownedNotes(profile.id).subscribe({ next: () => resolve(), error: () => resolve() }),
          ),
          new Promise<void>((resolve) =>
            this.profileService.adoptUnownedSessions(profile.id).subscribe({ next: () => resolve(), error: () => resolve() }),
          ),
        ]);
      }
      this.adding.set(false);
      this.newName = "";
      if (!this.closable) this.profileService.select(profile);
    } catch {
      this.error.set("Could not create the profile. Is the Resource API running?");
    } finally {
      this.creating.set(false);
    }
  }
}
