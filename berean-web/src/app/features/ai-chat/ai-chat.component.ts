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

import {
  AgentHubService,
  HubState,
} from "../../core/services/agent-hub.service";
import type {
  RagIndexingEvent,
  RagIndexedEvent,
} from "../../core/services/agent-hub.service";
import { NavigationStateService } from "../../core/services/navigation-state.service";

export interface ChatMessage {
  role: "user" | "agent";
  text: string;
  streaming?: boolean;
}

const QUICK_ASKS = [
  {
    label: "Explain ↗",
    prompt:
      "Explain the theological significance of this passage in SDA context.",
  },
  {
    label: "Orig. lang ↗",
    prompt:
      "Show me the original Hebrew or Greek words for the selected verse and explain their meaning.",
  },
  {
    label: "EGW ↗",
    prompt: "What does Ellen G. White say about this passage?",
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

  @ViewChild("msgList") msgListRef!: ElementRef<HTMLElement>;

  readonly messages = signal<ChatMessage[]>([]);
  readonly inputText = signal("");
  readonly hubState = signal<HubState>("disconnected");
  readonly agentReady = signal(false);
  readonly ragChunks = signal(0);
  readonly cloudAvail = signal(false);
  readonly error = signal<string | null>(null);
  readonly isIndexing = signal(false);
  readonly indexingMessage = signal("");

  readonly quickAsks = QUICK_ASKS;

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

      this.hub.agentSelected$.subscribe((ev) => {
        this.agentReady.set(true);
        this.cloudAvail.set(ev.cloudAvailable);
        this.ragChunks.set(ev.ragChunks);
        this.error.set(null);
      }),

      this.hub.token$.subscribe((token) => {
        this.messages.update((msgs) => {
          const last = msgs[msgs.length - 1];
          if (last?.role === "agent" && last.streaming) {
            return [...msgs.slice(0, -1), { ...last, text: last.text + token }];
          }
          // Start a new streaming bubble
          return [...msgs, { role: "agent", text: token, streaming: true }];
        });
        this.shouldScroll = true;
      }),

      this.hub.complete$.subscribe(() => {
        this.messages.update((msgs) => {
          const last = msgs[msgs.length - 1];
          if (last?.streaming) {
            return [...msgs.slice(0, -1), { ...last, streaming: false }];
          }
          return msgs;
        });
        this.shouldScroll = true;
      }),

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
      await this.hub.selectBibleAgent();
    } catch {
      this.error.set("Failed to initialise the Bible agent.");
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
      await this.hub.sendMessage(withContext);
    } catch {
      this.error.set("Failed to send message.");
    }
  }

  quickAsk(prompt: string): void {
    if (!this.agentReady() || this.isStreaming()) return;
    this.send(prompt);
  }

  async reset(): Promise<void> {
    await this.hub.resetConversation();
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
