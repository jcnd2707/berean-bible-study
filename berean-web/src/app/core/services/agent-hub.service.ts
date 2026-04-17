import { Injectable, NgZone, OnDestroy } from "@angular/core";
import * as signalR from "@microsoft/signalr";
import { Subject, BehaviorSubject } from "rxjs";

export type HubState =
  | "disconnected"
  | "connecting"
  | "connected"
  | "reconnecting";

export interface AgentSelectedEvent {
  agentType: string;
  cloudAvailable: boolean;
  ragChunks: number;
}

@Injectable({ providedIn: "root" })
export class AgentHubService implements OnDestroy {
  private readonly HUB_URL = "http://localhost:5050/hubs/chat";

  private hub!: signalR.HubConnection;

  readonly state$ = new BehaviorSubject<HubState>("disconnected");
  readonly token$ = new Subject<string>();
  readonly complete$ = new Subject<string>();
  readonly agentSelected$ = new Subject<AgentSelectedEvent>();
  readonly error$ = new Subject<string>();
  readonly reset$ = new Subject<void>();

  constructor(private zone: NgZone) {
    this.buildConnection();
  }

  private buildConnection(): void {
    this.hub = new signalR.HubConnectionBuilder()
      .withUrl(this.HUB_URL)
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    this.hub.on("TokenReceived", (t: string) =>
      this.zone.run(() => this.token$.next(t)),
    );
    this.hub.on("MessageComplete", (t: string) =>
      this.zone.run(() => this.complete$.next(t)),
    );
    this.hub.on(
      "AgentSelected",
      (type: string, cloud: boolean, chunks: number) =>
        this.zone.run(() =>
          this.agentSelected$.next({
            agentType: type,
            cloudAvailable: cloud,
            ragChunks: chunks,
          }),
        ),
    );
    this.hub.on("Error", (msg: string) =>
      this.zone.run(() => this.error$.next(msg)),
    );
    this.hub.on("ConversationReset", () =>
      this.zone.run(() => this.reset$.next()),
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

  async selectBibleAgent(): Promise<void> {
    await this.hub.send("SelectAgent", "Bible");
  }

  async sendMessage(text: string): Promise<void> {
    await this.hub.send("SendMessage", text);
  }

  async resetConversation(): Promise<void> {
    await this.hub.send("ResetConversation");
  }

  async reconnect(): Promise<void> {
    if (this.hub.state !== signalR.HubConnectionState.Disconnected) return;
    await this.connect();
  }

  async disconnect(): Promise<void> {
    await this.hub.stop();
  }

  ngOnDestroy(): void {
    this.hub.stop();
  }
}
