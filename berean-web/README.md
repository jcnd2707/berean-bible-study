# Berean Web

Angular 19 client for the Berean Bible study app. Run it after the two APIs; see the [root README](../README.md) for what everything does and how to configure it.

## Setup

```bash
npm install
npm start          # http://localhost:4200
```

Prerequisites: Node.js 20+, npm 10+.

## Where it looks for the servers

`src/environments/environment.ts`:

| Setting | Default | Server |
|---|---|---|
| `apiBaseUrl` | `http://localhost:5121` | `BereanResource.Api` (Bible, commentary, dictionary, books, notes) |
| `agentApiUrl` | `http://localhost:5050` | `Berean.Agent.Api` (SignalR chat hub at `/hubs/chat`) |

`BereanResource.Api` must allow the origin the app is served from (`Cors:AllowedOrigins`), and `Berean.Agent.Api` allows `http://localhost:4200` by default.

## Layout

```
src/app/
├── core/
│   ├── models/                  API shapes
│   └── services/
│       ├── agent-hub.service.ts       SignalR connection to the chat hub (events, conversations)
│       ├── navigation-state.service.ts  current passage, and one-shot requests panels act on
│       └── …                          one service per REST area (bible, commentary, dictionary, books, notes)
└── features/
    ├── bible-reader/    chapters, translation tabs, Strong's numbers under words, compare
    ├── commentary/      commentary panel (opens on a chat citation)
    ├── books/           prose books reader (opens on a chat citation)
    ├── dictionary/, cross-references/, notes/, search/, compare/
    ├── right-panel/     commentary / notes / cross-refs tabs
    └── ai-chat/         the study assistant: modes, citations, sources, saved conversations
```

## Chat behaviour worth knowing

- **Modes:** Quick (no lookup), Deep (sources first), Compare (traditions side by side). The **Perspective** dropdown (shown only when the Agent has one configured) adds a separate section drawing only on that perspective's own sources; it's chosen once per conversation and locked once the first question is sent — a different perspective means a new conversation.
- **Citations:** `[S1]` in an answer becomes a chip showing the source and its tradition. Clicking a commentary chip goes to that verse and opens that commentary; a book chip opens the book at that chapter. The sources are listed under each answer, grouped by tradition.
- **Save to notes:** appends the answer, its sources and the date to the note for the passage you are reading (it never replaces an existing note).
- **Conversations:** saved automatically once the first question is answered; the clock button lists them. The last one reopens on refresh.
- **Strong's:** the "Strong's" toggle in the reader shows the number under each tagged word; clicking one opens it in the dictionary panel.
