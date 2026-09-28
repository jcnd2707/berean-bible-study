import { Component, OnDestroy, OnInit, computed, inject, signal } from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { Subject, Subscription } from "rxjs";
import { debounceTime, distinctUntilChanged } from "rxjs/operators";

import { AgentHubService, ConversationSummary } from "../../core/services/agent-hub.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { groupByDate, SessionDateGroup } from "../../core/services/session-date-grouping";
import { BibleLocation } from "../../core/models";

/**
 * "Study sessions" (PROFILES_AND_SESSIONS_PLAN.md Phase 4): a saved conversation, enriched —
 * search, pin, rename, and opening one returns the reader to where it ended (LastLocation), not
 * just where it began.
 */
@Component({
  selector: "app-sessions-list",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./sessions-list.component.html",
  styleUrl: "./sessions-list.component.scss",
})
export class SessionsListComponent implements OnInit, OnDestroy {
  private readonly hub = inject(AgentHubService);
  readonly nav = inject(NavigationStateService);

  readonly sessions = signal<ConversationSummary[]>([]);
  readonly filterText = signal("");
  readonly loading = signal(true);

  readonly renamingId = signal<string | null>(null);
  renameText = "";
  readonly confirmingDeleteId = signal<string | null>(null);

  readonly pinned = computed(() => this.sessions().filter((s) => s.pinned));
  readonly groups = computed<SessionDateGroup<ConversationSummary>[]>(() =>
    groupByDate(this.sessions().filter((s) => !s.pinned)),
  );

  private readonly filter$ = new Subject<string>();
  private subs: Subscription[] = [];

  ngOnInit(): void {
    this.subs.push(
      this.hub.conversationList$.subscribe((list) => {
        this.sessions.set(list);
        this.loading.set(false);
      }),
      this.filter$.pipe(debounceTime(300), distinctUntilChanged()).subscribe((q) => {
        this.hub.listConversations(q || undefined).catch(() => {});
      }),
    );
    this.hub.listConversations().catch(() => this.loading.set(false));
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
  }

  onFilterInput(value: string): void {
    this.filterText.set(value);
    this.filter$.next(value);
  }

  /** Opens a session: returns the reader to where it ended, switches to Ask, and shows the "Continuing…" banner. */
  open(s: ConversationSummary): void {
    const loc = this.parseLocation(s.lastLocation);
    if (loc) this.nav.navigate(loc);
    this.nav.requestRightTab("ask");
    this.nav.announceContinuing({ title: s.title, passage: s.passage, lastStudiedIso: s.updatedAt });
    this.hub.resumeConversation(s.id).catch(() => {});
    this.nav.closeSessions();
  }

  startRename(event: Event, s: ConversationSummary): void {
    event.stopPropagation();
    this.renamingId.set(s.id);
    this.renameText = s.title;
  }

  saveRename(s: ConversationSummary): void {
    const title = this.renameText.trim();
    this.renamingId.set(null);
    if (!title || title === s.title) return;
    this.hub.renameConversation(s.id, title).catch(() => {});
  }

  cancelRename(event: Event): void {
    event.stopPropagation();
    this.renamingId.set(null);
  }

  togglePin(event: Event, s: ConversationSummary): void {
    event.stopPropagation();
    this.hub.setPinned(s.id, !s.pinned).catch(() => {});
  }

  askDelete(event: Event, s: ConversationSummary): void {
    event.stopPropagation();
    this.confirmingDeleteId.set(s.id);
  }

  confirmDelete(event: Event, s: ConversationSummary): void {
    event.stopPropagation();
    this.confirmingDeleteId.set(null);
    this.hub.deleteConversation(s.id).catch(() => {});
  }

  cancelDelete(event: Event): void {
    event.stopPropagation();
    this.confirmingDeleteId.set(null);
  }

  formatDate(iso: string): string {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? "" : d.toLocaleDateString();
  }

  snippet(text: string): string {
    return text.length > 120 ? text.slice(0, 120).trimEnd() + "…" : text;
  }

  private parseLocation(json: string | null): BibleLocation | null {
    if (!json) return null;
    try {
      const parsed = JSON.parse(json);
      if (!parsed?.book || !parsed?.chapter) return null;
      return {
        moduleId: parsed.moduleId ?? "",
        book: parsed.book,
        chapter: parsed.chapter,
        verse: parsed.verse ?? null,
      };
    } catch {
      return null;
    }
  }
}
