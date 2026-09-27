import {
  Component,
  OnInit,
  OnDestroy,
  inject,
  signal,
  computed,
  ViewChild,
  ElementRef,
  AfterViewChecked,
} from "@angular/core";
import { CommonModule } from "@angular/common";
import { FormsModule } from "@angular/forms";
import { Subscription } from "rxjs";
import { DomSanitizer, SafeHtml } from "@angular/platform-browser";

import {
  AgentHubService,
  HubState,
} from "../../core/services/agent-hub.service";
import type {
  ChatMode,
  ChatSource,
  ConversationSummary,
  RagIndexingEvent,
  RagIndexedEvent,
} from "../../core/services/agent-hub.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";
import { ModelService } from "../../core/services/model.service";
import { NotesService } from "../../core/services/notes.service";
import { renderAnswerHtml } from "./answer-html";

const CONVERSATION_KEY = "berean_conversationId";

export interface ChatMessage {
  role: "user" | "agent";
  text: string;
  streaming?: boolean;
  /** The numbered sources the answer may cite ([S1], [A1]…). */
  sources?: ChatSource[];
  /** What the model is doing right now ("Looking up hesed…"). */
  activity?: string;
  saved?: boolean;
}

const QUICK_ASKS_NEUTRAL = [
  {
    label: "Explain ↗",
    prompt: "Explain the theological significance of this passage.",
  },
  {
    label: "Orig. lang ↗",
    prompt:
      "Show me the original Hebrew or Greek words for the selected verse and explain their meaning.",
  },
  {
    label: "Context ↗",
    prompt: "What is the historical and cultural context of this passage?",
  },
];

const QUICK_ASKS_SDA = [
  {
    label: "EGW ↗",
    prompt: "What does Ellen G. White say about this passage?",
  },
  {
    label: "SDA view ↗",
    prompt: "What is the Seventh-day Adventist interpretation of this passage?",
  },
];

@Component({
  selector: "app-ai-chat",
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: "./ai-chat.component.html",
  styleUrl: "./ai-chat.component.scss",
})
export class AiChatComponent implements OnInit, OnDestroy, AfterViewChecked {
  private readonly hub = inject(AgentHubService);
  readonly nav = inject(NavigationStateService);
  readonly modelService = inject(ModelService);
  private readonly notes = inject(NotesService);
  private readonly sanitizer = inject(DomSanitizer);

  @ViewChild("msgList") msgListRef!: ElementRef<HTMLElement>;

  readonly messages = signal<ChatMessage[]>([]);
  readonly inputText = signal("");
  readonly hubState = signal<HubState>("disconnected");
  readonly agentReady = signal(false);
  readonly ragChunks = signal(0);
  readonly error = signal<string | null>(null);
  readonly isIndexing = signal(false);
  readonly indexingMessage = signal("");
  readonly modes: readonly ChatMode[] = ["Quick", "Deep", "Compare"];
  readonly mode = signal<ChatMode>("Quick");
  readonly includeSDA = signal(false);

  // ── Saved conversations ──
  readonly conversations = signal<ConversationSummary[]>([]);
  readonly currentConversationId = signal<string | null>(null);
  readonly showHistory = signal(false);

  readonly quickAsks = computed(() =>
    this.includeSDA()
      ? [...QUICK_ASKS_NEUTRAL, ...QUICK_ASKS_SDA]
      : QUICK_ASKS_NEUTRAL,
  );

  readonly contextLabel = computed(() => {
    const loc = this.nav.location();
    if (!loc) return "No passage selected";
    const verse = loc.verse ? `:${loc.verse}` : " (click a verse to select)";
    return `${loc.book} ${loc.chapter}${verse} · ${loc.moduleId}`;
  });

  readonly canSend = computed(
    () =>
      this.agentReady() &&
      this.hubState() === "connected" &&
      this.inputText().trim().length > 0 &&
      !this.isStreaming(),
  );

  readonly isStreaming = computed(() =>
    this.messages().some((m) => m.streaming),
  );

  private subs: Subscription[] = [];
  private shouldScroll = false;

  ngOnInit(): void {
    this.subs.push(
      this.hub.state$.subscribe((s) => {
        this.hubState.set(s);
        if (s === "connected" && !this.agentReady()) {
          this.initAgent();
        }
        if (s === "disconnected") {
          this.agentReady.set(false);
        }
      }),

      this.hub.sessionStarted$.subscribe((ev) => {
        this.agentReady.set(true);
        this.ragChunks.set(ev.ragChunks);
        this.error.set(null);
      }),

      this.hub.token$.subscribe((token) => {
        this.messages.update((msgs) => {
          const last = msgs[msgs.length - 1];
          if (last?.role === "agent" && last.streaming) {
            return [
              ...msgs.slice(0, -1),
              { ...last, text: last.text + token, activity: token ? undefined : last.activity },
            ];
          }
          // Start a new streaming bubble
          return [...msgs, { role: "agent", text: token, streaming: true }];
        });
        this.shouldScroll = true;
      }),

      this.hub.conversationStarted$.subscribe((id) => {
        this.currentConversationId.set(id);
        this.rememberConversation(id);
        this.messages.set([]);
        this.error.set(null);
        this.showHistory.set(false);
        this.hub.listConversations().catch(() => {});
      }),

      this.hub.conversationLoaded$.subscribe((ev) => {
        this.currentConversationId.set(ev.id);
        this.rememberConversation(ev.id);
        this.messages.set(
          ev.messages.map((m) => ({
            role: m.role,
            text: m.text,
            sources: m.sources ?? undefined,
          })),
        );
        if (ev.modelId) this.modelService.selectModel(ev.modelId);
        this.error.set(null);
        this.showHistory.set(false);
        this.shouldScroll = true;
        this.hub.listConversations().catch(() => {});
      }),

      this.hub.conversationList$.subscribe((list) => this.conversations.set(list)),

      this.hub.conversationDeleted$.subscribe((id) => {
        this.conversations.update((list) => list.filter((c) => c.id !== id));
        if (id === this.currentConversationId()) {
          this.forgetConversation();
          this.hub.startConversation(this.modelService.selectedModelId()).catch(() => {});
        }
      }),

      this.hub.complete$.subscribe(() => {
        // The first answer is what creates a saved conversation, so refresh the list.
        this.hub.listConversations().catch(() => {});
        this.messages.update((msgs) => {
          const last = msgs[msgs.length - 1];
          if (last?.streaming) {
            return [...msgs.slice(0, -1), { ...last, streaming: false }];
          }
          return msgs;
        });
        this.shouldScroll = true;
      }),

      // The sources the answer may cite arrive before its text.
      this.hub.sources$.subscribe((sources) =>
        this.updateStreamingMessage((m) => ({ ...m, sources })),
      ),

      this.hub.toolActivity$.subscribe((ev) =>
        this.updateStreamingMessage((m) => ({ ...m, activity: ev.text })),
      ),

      this.hub.error$.subscribe((msg) => {
        this.error.set(msg);
        // Remove any partial streaming bubble
        this.messages.update((msgs) => msgs.filter((m) => !m.streaming));
      }),

      this.hub.reset$.subscribe(() => {
        this.messages.set([]);
        this.error.set(null);
      }),

      this.hub.ragIndexing$.subscribe((ev: RagIndexingEvent) => {
        this.isIndexing.set(true);
        this.indexingMessage.set(ev.message);
      }),

      this.hub.ragIndexed$.subscribe((ev: RagIndexedEvent) => {
        this.isIndexing.set(false);
        this.indexingMessage.set("");
        if (ev.success) {
          // Fetch the real chunk count now that indexing is done
          this.hub.getRagStatus().catch(() => {});
        }
      }),

      this.hub.ragStatus$.subscribe((ev) => {
        this.ragChunks.set(ev.chunkCount);
      }),
    );

    this.hub.connect().catch(() => {
      this.error.set("Could not connect to the Agent hub at localhost:5050.");
    });
  }

  ngAfterViewChecked(): void {
    if (this.shouldScroll) {
      this.scrollToBottom();
      this.shouldScroll = false;
    }
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
  }

  private async initAgent(): Promise<void> {
    try {
      await this.modelService.ready;
      const saved = this.savedConversationId();
      if (saved) {
        // Pick up where the last visit left off (a refresh, a reconnect or a restart).
        await this.hub.resumeConversation(saved);
      } else {
        await this.hub.startConversation(this.modelService.selectedModelId());
      }
      await this.hub.listConversations();
    } catch {
      this.error.set("Failed to initialise the Bible agent.");
    }
  }

  async onModelChange(modelId: string): Promise<void> {
    this.modelService.selectModel(modelId);
    this.agentReady.set(false);
    this.error.set(null);
    try {
      await this.hub.startConversation(modelId);
    } catch {
      this.error.set("Failed to switch model.");
    }
  }

  async send(overridePrompt?: string): Promise<void> {
    const raw = overridePrompt ?? this.inputText().trim();
    if (!raw || !this.agentReady()) return;

    const withContext = this.buildMessage(raw);

    this.messages.update((msgs) => [...msgs, { role: "user", text: raw }]);
    this.inputText.set("");
    this.error.set(null);
    this.shouldScroll = true;

    try {
      await this.hub.sendMessage(withContext, this.mode(), this.includeSDA());
    } catch {
      this.error.set("Failed to send message.");
    }
  }

  setMode(mode: ChatMode): void {
    this.mode.set(mode);
  }

  modeHint(mode: ChatMode): string {
    switch (mode) {
      case "Quick":
        return "Answer directly, without looking up sources";
      case "Deep":
        return "Look up commentaries and the exact verse text first";
      case "Compare":
        return "Set the main traditions' readings side by side";
    }
  }

  toggleSDA(): void {
    this.includeSDA.update((v) => !v);
  }

  quickAsk(prompt: string): void {
    if (!this.agentReady() || this.isStreaming()) return;
    this.send(prompt);
  }

  /** Starts a fresh conversation. The old one stays in the history. */
  async reset(): Promise<void> {
    await this.hub.resetConversation();
  }

  toggleHistory(): void {
    this.showHistory.update((v) => !v);
    if (this.showHistory()) this.hub.listConversations().catch(() => {});
  }

  openConversation(id: string): void {
    if (id === this.currentConversationId()) {
      this.showHistory.set(false);
      return;
    }
    this.agentReady.set(false);
    this.hub.resumeConversation(id).catch(() => this.error.set("Could not open that conversation."));
  }

  deleteConversation(event: Event, id: string): void {
    event.stopPropagation();
    this.hub.deleteConversation(id).catch(() => this.error.set("Could not delete that conversation."));
  }

  conversationDate(c: ConversationSummary): string {
    const d = new Date(c.updatedAt);
    return isNaN(d.getTime()) ? "" : d.toLocaleDateString();
  }

  private savedConversationId(): string | null {
    try {
      return localStorage.getItem(CONVERSATION_KEY);
    } catch {
      return null;
    }
  }

  private rememberConversation(id: string): void {
    try {
      localStorage.setItem(CONVERSATION_KEY, id);
    } catch {}
  }

  private forgetConversation(): void {
    try {
      localStorage.removeItem(CONVERSATION_KEY);
    } catch {}
  }

  onKeydown(event: KeyboardEvent): void {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      this.send();
    }
  }

  setInput(value: string): void {
    this.inputText.set(value);
  }

  /** Prepend rich context to the message so the agent knows exactly what is being viewed */
  private buildMessage(text: string): string {
    const loc = this.nav.location();
    if (!loc) return text;

    const lines: string[] = [];
    lines.push(`Translation: ${loc.moduleId}`);
    lines.push(`Passage: ${loc.book} chapter ${loc.chapter}`);

    if (loc.verse) {
      lines.push(`Selected verse: ${loc.book} ${loc.chapter}:${loc.verse}`);
      const verseText = this.nav.activeVerseText();
      if (verseText) lines.push(`Verse text: "${verseText}"`);
    }

    // Include active commentary entry if one covers the selected verse
    const comm = this.nav.activeCommentary();
    const commMod = this.nav.activeCommentaryMod();
    if (comm && commMod) {
      const snippet = comm.text.slice(0, 600).replace(/\s+/g, " ").trim();
      lines.push(`Commentary (${commMod}, ${comm.reference}): ${snippet}…`);
    }

    // Include dictionary result if a word was looked up
    const word = this.nav.activeWord();
    if (word) {
      const defSnippet = word.definition
        .slice(0, 300)
        .replace(/\s+/g, " ")
        .trim();
      lines.push(`Dictionary (${word.source}) "${word.word}": ${defSnippet}…`);
    }

    const ctx = lines.map((l) => `[${l}]`).join("\n");
    return `${ctx}\n\n${text}`;
  }

  /** Applies a change to the answer being streamed, opening its bubble first if needed. */
  private updateStreamingMessage(change: (m: ChatMessage) => ChatMessage): void {
    this.messages.update((msgs) => {
      const last = msgs[msgs.length - 1];
      if (last?.role === "agent" && last.streaming) {
        return [...msgs.slice(0, -1), change(last)];
      }
      return [...msgs, change({ role: "agent", text: "", streaming: true })];
    });
    this.shouldScroll = true;
  }

  // ── Citations ────────────────────────────────────────────────────────────

  private readonly htmlCache = new WeakMap<
    ChatMessage,
    { text: string; sources: ChatSource[] | undefined; html: SafeHtml }
  >();

  /**
   * The answer as HTML: light markdown plus [S1]-style citations as chips. Everything from the
   * model is escaped inside renderAnswerHtml, so trusting the result is safe.
   */
  answerHtml(msg: ChatMessage): SafeHtml {
    const cached = this.htmlCache.get(msg);
    if (cached && cached.text === msg.text && cached.sources === msg.sources)
      return cached.html;

    const html = this.sanitizer.bypassSecurityTrustHtml(
      renderAnswerHtml(
        msg.text,
        msg.sources,
        (s) => this.chipLabel(s),
        (s) => this.isOpenable(s),
      ),
    );
    this.htmlCache.set(msg, { text: msg.text, sources: msg.sources, html });
    return html;
  }

  /** Clicks on chips inside the rendered answer (they are plain buttons carrying data-cite). */
  onAnswerClick(event: MouseEvent, msg: ChatMessage): void {
    const chip = (event.target as HTMLElement | null)?.closest("[data-cite]");
    const id = chip?.getAttribute("data-cite");
    const source = id ? msg.sources?.find((s) => s.id === id) : undefined;
    if (source) this.openSource(source);
  }

  /** "Barnes' Notes on the Bible · Evangelical", or the book title for a book chapter. */
  chipLabel(s: ChatSource): string {
    const name =
      s.kind === "book" ? s.label.replace(/\s*\([^)]*\)\s*$/, "") : s.displayName;
    return `${name} · ${s.tradition}`;
  }

  isOpenable(s: ChatSource): boolean {
    return (
      (s.kind === "commentary" && s.bookNumber !== null && s.chapter !== null) ||
      (s.kind === "book" && s.bookChapterIndex !== null)
    );
  }

  /** Commentary: go to the verse and open that commentary. Book: open the book at that chapter. */
  openSource(s: ChatSource): void {
    if (!this.isOpenable(s)) return;
    if (s.kind === "commentary") {
      this.nav.openCommentary(s.moduleId, s.bookNumber!, s.chapter!, s.verse);
    } else {
      this.nav.openBookChapter(s.moduleId, s.bookChapterIndex ?? 1);
    }
  }

  /** The sources of an answer grouped by tradition, so balance (or the lack of it) shows at a glance. */
  groupedSources(msg: ChatMessage): { tradition: string; sources: ChatSource[] }[] {
    const groups = new Map<string, ChatSource[]>();
    for (const s of msg.sources ?? []) {
      const list = groups.get(s.tradition) ?? [];
      list.push(s);
      groups.set(s.tradition, list);
    }
    return [...groups].map(([tradition, sources]) => ({ tradition, sources }));
  }

  // ── Save to notes ────────────────────────────────────────────────────────

  /** Appends the answer, its sources and the date to the note for the passage being read. */
  saveToNotes(index: number): void {
    const msg = this.messages()[index];
    const loc = this.nav.location();
    if (!msg || !loc) return;

    const question = this.messages()[index - 1]?.role === "user" ? this.messages()[index - 1].text : "";
    const reference = NotesService.toReference(loc.book, loc.chapter, loc.verse);

    this.notes.append(reference, this.formatForNotes(msg, question)).subscribe({
      next: () => {
        this.messages.update((msgs) =>
          msgs.map((m, i) => (i === index ? { ...m, saved: true } : m)),
        );
        // Keep the "has a note" markers in the reader current.
        this.notes
          .getAll()
          .subscribe((all) => this.nav.setNotedReferences(all.map((n) => n.reference)));
      },
      error: () => this.error.set("Could not save the note."),
    });
  }

  private formatForNotes(msg: ChatMessage, question: string): string {
    const lines: string[] = [];
    if (question) lines.push(`**${question.trim()}**`, "");
    lines.push(msg.text.trim(), "");
    if (msg.sources?.length) {
      lines.push("Sources:");
      for (const s of msg.sources) lines.push(`- [${s.id}] ${s.label}`);
      lines.push("");
    }
    lines.push(`_Saved from the study assistant on ${new Date().toISOString().slice(0, 10)}_`);
    return lines.join("\n");
  }

  private scrollToBottom(): void {
    const el = this.msgListRef?.nativeElement;
    if (el) el.scrollTop = el.scrollHeight;
  }

  async reconnect(): Promise<void> {
    this.error.set(null);
    this.agentReady.set(false);
    try {
      await this.hub.reconnect();
    } catch {
      this.error.set("Could not reconnect. Is the Agent API running?");
    }
  }

  stateLabel(): string {
    switch (this.hubState()) {
      case "connecting":
        return "Connecting…";
      case "reconnecting":
        return "Reconnecting…";
      case "connected":
        return this.agentReady() ? "Bible agent ready" : "Initialising…";
      default:
        return "Disconnected";
    }
  }

  stateDot(): string {
    switch (this.hubState()) {
      case "connected":
        return this.agentReady() ? "dot--ready" : "dot--busy";
      case "connecting":
      case "reconnecting":
        return "dot--busy";
      default:
        return "dot--off";
    }
  }
}
