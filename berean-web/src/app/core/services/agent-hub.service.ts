import { Injectable, NgZone, OnDestroy, signal } from "@angular/core";
import * as signalR from "@microsoft/signalr";
import { Subject, BehaviorSubject } from "rxjs";
import { environment } from "../../../environments/environment";
import { BibleLocation } from "../models";

const PROFILE_STORAGE_KEY = "berean_profileId";

/** How a question is answered: Quick skips retrieval, Deep retrieves sources, Compare sets traditions side by side. */
export type ChatMode = "Quick" | "Deep" | "Compare";

export type HubState =
  | "disconnected"
  | "connecting"
  | "connected"
  | "reconnecting";

/** A numbered source the answer may cite ([S1], [A1]…), as sent by the server. */
export interface ChatSource {
  id: string;
  kind: "commentary" | "book" | "dictionary";
  moduleId: string;
  displayName: string;
  tradition: string;
  era: string | null;
  label: string;
  book: string | null;
  bookNumber: number | null;
  chapter: number | null;
  verse: number | null;
  bookChapterIndex: number | null;
}

/** A saved conversation, as listed in the history — a "study session" in the UI. */
export interface ConversationSummary {
  id: string;
  title: string;
  updatedAt: string;
  passage: string | null;
  modelId: string | null;
  /** Where the study ended (JSON — see BibleLocation), or null if no location was ever recorded. */
  lastLocation: string | null;
  pinned: boolean;
  /** Hit its question or context limit (Phase 5) — read-only until continued. */
  full: boolean;
  /** Written once the session is continued; shown as the row's preview when present. */
  recap: string | null;
}

/** Where the active session stands against Sessions:MaxQuestions / MaxContextTokens (Phase 5). */
export interface SessionLimitState {
  questionsUsed: number;
  maxQuestions: number;
  contextTokens: number;
  maxContextTokens: number;
  state: "ok" | "nearing" | "full";
}

/** Sent alongside ContinueConversation's own ConversationStarted — the session it replaces. */
export interface ConversationContinuedEvent {
  previousId: string;
  previousTitle: string;
  recap: string;
}

/** A message of a reopened conversation. */
export interface StoredChatMessage {
  role: "user" | "agent";
  text: string;
  sources: ChatSource[] | null;
}

export interface ConversationLoadedEvent {
  id: string;
  title: string;
  modelId: string | null;
  messages: StoredChatMessage[];
  /** The conversation's locked-in perspective selection (see StartConversation). */
  perspectives: string[];
  lastLocation: string | null;
  pinned: boolean;
}

export interface ToolActivityEvent {
  name: string;
  text: string;
}

export interface SessionStartedEvent {
  ragChunks: number;
}

export interface RagIndexingEvent {
  message: string;
}

export interface RagIndexedEvent {
  success: boolean;
  message: string;
}

export interface RagStatusEvent {
  hasIndex: boolean;
  chunkCount: number;
  details: string;
}

@Injectable({ providedIn: "root" })
export class AgentHubService implements OnDestroy {
  private readonly HUB_URL = `${environment.agentApiUrl}/hubs/chat`;

  private hub!: signalR.HubConnection;

  readonly state$ = new BehaviorSubject<HubState>("disconnected");
  readonly token$ = new Subject<string>();
  readonly complete$ = new Subject<string>();
  readonly sources$ = new Subject<ChatSource[]>();
  readonly conversationStarted$ = new Subject<string>();
  readonly conversationList$ = new Subject<ConversationSummary[]>();
  readonly conversationLoaded$ = new Subject<ConversationLoadedEvent>();
  readonly conversationDeleted$ = new Subject<string>();
  readonly toolActivity$ = new Subject<ToolActivityEvent>();
  readonly sessionStarted$ = new Subject<SessionStartedEvent>();
  readonly error$ = new Subject<string>();
  readonly ragIndexing$ = new Subject<RagIndexingEvent>();
  readonly ragIndexed$ = new Subject<RagIndexedEvent>();
  readonly ragStatus$ = new Subject<RagStatusEvent>();
  readonly sessionLimit$ = new Subject<SessionLimitState>();
  readonly conversationContinued$ = new Subject<ConversationContinuedEvent>();

  /** True from sendMessage() until the answer completes or errors — see PROFILES_AND_SESSIONS_PLAN.md D4. */
  readonly isAnswering = signal(false);

  constructor(private zone: NgZone) {
    this.complete$.subscribe(() => this.isAnswering.set(false));
    this.error$.subscribe(() => this.isAnswering.set(false));
  }

  /**
   * Deliberately NOT called from the constructor: this service is also reached eagerly through
   * ProfileService (injected by AppComponent at bootstrap, before any profile is ever chosen), so
   * building the connection URL in the constructor would capture localStorage's profile id before
   * it's ever set — the hub would then always connect with no profile and get "Choose a profile
   * first." Building lazily, on first connect() (called only once the shell — gated on a profile
   * already being chosen — renders), guarantees the id is there by the time this runs.
   */
  private buildConnection(): void {
    // Browsers can't set headers on a WebSocket, so the profile travels as a query param instead
    // (D3), read once in ChatHub.OnConnectedAsync. The connection lives for the tab's lifetime and
    // a profile switch reloads the page (D4), so there's no need to rebuild this later.
    const profileId = this.currentProfileId();
    const url = profileId ? `${this.HUB_URL}?profile=${encodeURIComponent(profileId)}` : this.HUB_URL;

    this.hub = new signalR.HubConnectionBuilder()
      .withUrl(url)
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.hub.on("TokenReceived", (t: string) =>
      this.zone.run(() => this.token$.next(t)),
    );
    this.hub.on("ConversationStarted", (id: string) =>
      this.zone.run(() => this.conversationStarted$.next(id)),
    );
    this.hub.on("ConversationList", (list: ConversationSummary[]) =>
      this.zone.run(() => this.conversationList$.next(list)),
    );
    this.hub.on(
      "ConversationLoaded",
      (
        id: string,
        title: string,
        modelId: string | null,
        messages: StoredChatMessage[],
        perspectives: string[],
        lastLocation: string | null,
        pinned: boolean,
      ) =>
        this.zone.run(() =>
          this.conversationLoaded$.next({ id, title, modelId, messages, perspectives, lastLocation, pinned }),
        ),
    );
    this.hub.on("ConversationDeleted", (id: string) =>
      this.zone.run(() => this.conversationDeleted$.next(id)),
    );
    this.hub.on("Sources", (list: ChatSource[]) =>
      this.zone.run(() => this.sources$.next(list)),
    );
    this.hub.on("ToolActivity", (name: string, text: string) =>
      this.zone.run(() => this.toolActivity$.next({ name, text })),
    );
    this.hub.on("MessageComplete", (t: string) =>
      this.zone.run(() => this.complete$.next(t)),
    );
    this.hub.on("SessionStarted", (chunks: number) =>
      this.zone.run(() => this.sessionStarted$.next({ ragChunks: chunks })),
    );
    this.hub.on("Error", (msg: string) =>
      this.zone.run(() => this.error$.next(msg)),
    );
    this.hub.on("RagIndexing", (message: string) =>
      this.zone.run(() => this.ragIndexing$.next({ message })),
    );
    this.hub.on("RagIndexed", (success: boolean, message: string) =>
      this.zone.run(() => this.ragIndexed$.next({ success, message })),
    );
    this.hub.on("RagStatus", (hasIndex: boolean, chunkCount: number, details: string) =>
      this.zone.run(() => this.ragStatus$.next({ hasIndex, chunkCount, details })),
    );
    this.hub.on("SessionLimit", (state: SessionLimitState) =>
      this.zone.run(() => this.sessionLimit$.next(state)),
    );
    this.hub.on("ConversationContinued", (previousId: string, previousTitle: string, recap: string) =>
      this.zone.run(() => this.conversationContinued$.next({ previousId, previousTitle, recap })),
    );

    this.hub.onreconnecting(() =>
      this.zone.run(() => this.state$.next("reconnecting")),
    );
    this.hub.onreconnected(() =>
      this.zone.run(() => this.state$.next("connected")),
    );
    this.hub.onclose(() =>
      this.zone.run(() => this.state$.next("disconnected")),
    );
  }

  async connect(): Promise<void> {
    if (!this.hub) this.buildConnection();
    if (this.hub.state !== signalR.HubConnectionState.Disconnected) return;
    this.state$.next("connecting");
    try {
      await this.hub.start();
      this.state$.next("connected");
    } catch {
      this.state$.next("disconnected");
      throw new Error("Could not connect to Agent hub.");
    }
  }

  /**
   * Starts a new conversation on this model. The perspective selection (0 or 1 id today — see
   * PerspectiveService) is locked for the conversation's lifetime. Saved once its first question
   * is answered.
   */
  async startConversation(modelId: string, perspectives: string[] = []): Promise<void> {
    await this.hub.send("StartConversation", modelId, perspectives);
  }

  /** Reopens a saved conversation. */
  async resumeConversation(id: string): Promise<void> {
    await this.hub.send("ResumeConversation", id);
  }

  /** query matches a conversation's title or any of the profile's own questions in it. */
  async listConversations(query?: string): Promise<void> {
    await this.hub.send("ListConversations", query ?? null);
  }

  async deleteConversation(id: string): Promise<void> {
    await this.hub.send("DeleteConversation", id);
  }

  async renameConversation(id: string, title: string): Promise<void> {
    await this.hub.send("RenameConversation", id, title);
  }

  async setPinned(id: string, pinned: boolean): Promise<void> {
    await this.hub.send("SetPinned", id, pinned);
  }

  async sendMessage(text: string, mode: ChatMode = "Quick", location: BibleLocation | null = null): Promise<void> {
    this.isAnswering.set(true);
    await this.hub.send("SendMessage", text, mode, location);
  }

  /** Ends a full session and starts the next part of the same study, recap carried over (Phase 5). */
  async continueConversation(): Promise<void> {
    await this.hub.send("ContinueConversation");
  }

  private currentProfileId(): string | null {
    try {
      return localStorage.getItem(PROFILE_STORAGE_KEY);
    } catch {
      return null;
    }
  }

  async getRagStatus(): Promise<void> {
    await this.hub.send("GetRagStatus");
  }

  async reconnect(): Promise<void> {
    if (this.hub && this.hub.state !== signalR.HubConnectionState.Disconnected) return;
    await this.connect();
  }

  async disconnect(): Promise<void> {
    await this.hub?.stop();
  }

  ngOnDestroy(): void {
    this.hub?.stop();
  }
}
