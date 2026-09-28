# Berean

[![CI](https://github.com/jcnd2707/berean-bible-study/actions/workflows/ci.yml/badge.svg)](https://github.com/jcnd2707/berean-bible-study/actions/workflows/ci.yml)
<img width="3829" height="2081" alt="image" src="https://github.com/user-attachments/assets/90b049b0-1020-4f9b-923f-ca6b115c4d35" />


A Bible study app with an AI study assistant. The assistant answers from your own Bible study modules (Bibles, commentaries, dictionaries, prose books) and is built to be **even-handed**: it shows what the text says first, then how different traditions have read it, and it only brings in Adventist material when you turn that on.

> Imported from a private repository; the app itself, `Berean`, hasn't changed — only where the code lives.

## Solution layout

| Project | Type | Purpose |
|---|---|---|
| `Berean.Core/` | .NET 8 class library | The study agent: query routing, retrieval, tradition-aware indexing, prompts, tools, and the model providers |
| `Berean.Agent.Api/` | ASP.NET Core 8 | SignalR hub (`/hubs/chat`): conversations, streaming answers, sources, saved chats |
| `BereanResource.Api/` | ASP.NET Core 8 Web API | REST access to your Bible study modules: Bible, commentary, dictionary, cross-references, books, notes, Strong's occurrences, module profiles |
| `berean-web/` | Angular 20 + Tailwind 4 | Web client: Bible reader, commentary, dictionary, cross-references, notes, compare, AI chat |
| `Berean.Core.Tests/` | xUnit | Unit tests (router, retrieval, indexing, agent, Claude Code client, conversation store) |
| `eval/Berean.Eval/` | console | The evaluation harness (see below) |
| `tests-support/FakeClaude/` | console | A stand-in for the `claude` CLI, used only by the tests |


```
 berean-web (4200) ──REST──▶ BereanResource.Api (5121) ──▶ Bible study modules, notes.db
        │                             ▲
        └───SignalR──▶ Berean.Agent.Api (5050) ── uses Berean.Core
                          ├─▶ BereanResource.Api : verse text, commentary, word lookups, module profiles
                          ├─▶ Ollama (11434)     : embeddings (always local)
                          └─▶ the answering model: Claude Code | Anthropic API | OpenAI | Ollama
```

## How an answer is built

1. **Route.** [QueryRouter](Berean.Core/Routing/QueryRouter.cs) reads the question with regexes (no model call): a verse reference (any number of them, with ranges), a word to look up, or a general question.
2. **Retrieve, in passes.**
   - *Main pass (neutral):* the exact verse text and every commentary's entry for the verse, read straight from the Resource API (so each tradition counts equally); plus semantic search of the vector index, excluding every configured [perspective's](#perspectives) tradition and capping how much any one module can add (`MaxPerModule`, default 2). Dictionary and lexicon entries are looked up for the words asked about.
   - *Perspective pass:* one per perspective selected for the conversation (see [Perspectives](#perspectives) below). The same question restricted to that perspective's tradition, returned as its own `<LABEL> SOURCES` block, with its own citation prefix and its own budget on top of the neutral material.
3. **Label.** Every source is numbered and tagged with its tradition and era, e.g. `[S1] Barnes' Notes on the Bible (Evangelical, 19th c.) — John 3:16`. A perspective's sources use its own prefix, e.g. `[ADV1]…`.
4. **Answer.** The model gets the material *before* the question. The system prompt ([system.md](Berean.Core/Agent/Prompts/system.md)) is about method: text first, then the main views at their strongest with who holds each, keeping what the text states apart from inference and dispute, never treating one tradition as "the" view, citing only sources it was given.
5. **Modes.** *Quick* skips retrieval. *Deep* retrieves. *Compare* sets the interpretive traditions side by side in a fixed structure.

The web client turns `[S1]`-style citations into chips that open the commentary or book chapter, lists the sources under each answer grouped by tradition, and can save an answer to the note for the passage you are reading.

### Tradition labels

Each module's tradition comes from `ModuleProfiles` in `BereanResource.Api/appsettings.json`:

```json
"barnes": { "Tradition": "Evangelical", "Era": "19th c.", "DisplayName": "Barnes' Notes on the Bible" }
```

Traditions: `Adventist`, `Reformed`, `Wesleyan`, `Baptist`, `Lutheran`, `Evangelical`, `Catholic`, `Orthodox`, `Jewish`, `Academic`, `Lexical`, `Unclassified`. `GET /api/resources/profiles/unclassified` lists modules that still need a label. Tags are stored on every chunk in the index; changing a label is applied to the index at the next start (no re-embedding).

### Perspectives

Any tradition can be set up as a separate, opt-in section — the public config ships with none configured. Add one under `Perspectives` in `Berean.Agent.Api/appsettings.json` (or your local `appsettings.Development.json`):

```json
"Perspectives": [
  { "Id": "adventist", "Tradition": "Adventist", "Label": "Seventh-day Adventist", "CitationPrefix": "ADV", "TopK": 4 }
],
"MaxPerspectivesPerQuestion": 1
```

A perspective is chosen once, when a conversation starts (the web client's "Perspective" dropdown, shown only when at least one is configured), and is locked for that conversation's lifetime — a different perspective means a new conversation. `GET /api/perspectives` lists what's configured. Whether or not one is selected, every configured perspective's tradition is always held out of the neutral pass, so switching a perspective on never changes what the neutral analysis draws on.

## Choosing the model

Configured under `Llm` in `Berean.Agent.Api/appsettings.json`. Ollama always does the embeddings; only *answering* moves.

| Provider | What it uses | Notes |
|---|---|---|
| `ClaudeCode` (default) | The `claude` CLI in headless mode, using the login already on this machine | No API key; usage counts against your subscription's limits. **Personal use on your own machine only**; check Anthropic's current terms. |
| `Anthropic` | Anthropic's API (official SDK) | Needs `ANTHROPIC_API_KEY` in the environment. Prompt caching and effort are set. |
| `OpenAI` | OpenAI's API | Needs `OPENAI_API_KEY`. |
| `Ollama` | A local model | Works, but small local models ignore the prompt's method and skip citations. |

Claude Code runs locked down for plain text generation: no built-in tools, no MCP servers, no skills, no user or project settings (your own `CLAUDE.md` never reaches it), an empty working directory. Conversations resume the CLI session; if the session is gone, a new one starts with a condensed transcript.

## Prerequisites

- .NET 8 SDK
- Node.js 20+ and npm 10+
- [Ollama](https://ollama.com) with the embedding model: `ollama pull mxbai-embed-large`
- Bible study modules in a folder with `Bibles`, `Commentaries`, `Dictionaries`, `Lexicons` and `TopicNotes` subfolders (and `Books`): scrollmapper `.db` or MySword `.bbl` Bibles, MySword `.cmt` commentaries, MySword `.dct` dictionaries, and e-Sword `.lexi`/`.lexh` lexicons — or point `BereanResources:RootPath` at [`samples/`](samples/README.md) to try the app without sourcing your own library first
- For the default provider: the Claude Code CLI, logged in (`claude` on your PATH)

## Configuration

Committed `appsettings.json` files hold placeholders; put machine-specific values in `appsettings.Development.json` (or user-secrets). API keys are only ever read from environment variables.

**`BereanResource.Api`**: `BereanResources:RootPath`, `NotesDbPath`, `CrossReferencesDbFolder`; `ModuleProfiles`; `Cors:AllowedOrigins`. Profiles live in a `Profiles` table in the same `notes.db` (no separate config).

**`Berean.Agent.Api`**
- `Llm`: `Provider`, `Model`, `Models` (the ones the UI offers), `Effort`, `HistoryTurns`, and `ClaudeCode` (`ExecutablePath`, `WorkingDirectory`, `TimeoutSeconds`, `Effort`). On Windows an npm install of Claude Code puts a `claude.cmd` shim on the PATH; the real `claude.exe` beside it is used automatically.
- `Ollama`: `Endpoint`, `EmbeddingModel`, `DefaultModel`, `ToolCompatibleModels`.
- `Agents:BibleAgent`: `RagDbPath`, `ResourceApiBaseUrl` (required), `AllowedBibleModules`, `AllowedCommentaryModules` (empty = all), `MaxPerModule`, `TopK`, `MaxContextTokens`, `AutoIndexMissingModules`, chunk sizes.
- `Perspectives`, `MaxPerspectivesPerQuestion`: see [Perspectives](#perspectives) above.
- `Sessions`: `MaxQuestions` (default 12), `WarnQuestionsLeft` (3), `MaxContextTokens` (60000), `WarnContextFraction` (0.8) — the study-session length limit; see [Profiles & study sessions](#profiles--study-sessions) below. At startup, `Llm:HistoryTurns` is raised to at least `MaxQuestions` if it's configured lower (logged as a warning), so a question is never trimmed out of history before a session reaches its own limit.

## Running

```bash
dotnet run --project BereanResource.Api      # http://localhost:5121
dotnet run --project Berean.Agent.Api        # http://localhost:5050
cd berean-web && npm install && npm start    # http://localhost:4200
```

### The index

The vector index (`bible.rag.db`, next to it `chat.db` for saved conversations) is opened at the first chat. On start it:
1. adds the module/tradition columns to an older index, after copying it aside once (`bible.rag.db.pre-tradition.bak`);
2. tags existing rows from the module profiles (no re-embedding);
3. works out which modules aren't indexed yet, and logs them.

`chat.db` and `notes.db` get the same one-time treatment for profiles: the first start after upgrading backs each up (`chat.db.pre-profiles.bak`, `notes.db.pre-profiles.bak`) before adding the `ProfileId` column, and every row from before is left unowned rather than guessed at.

Indexing missing modules is **off by default** (`AutoIndexMissingModules: false`) because embedding runs at only a few chunks a second on CPU and a full commentary is hours of work. Verse questions don't need it: they read every commentary directly from the API. Semantic search over a commentary needs it indexed. Turn the setting on, or send `ReindexDocuments` from the hub, to build what's missing; nothing already indexed is touched, and an interrupted module is redone from scratch.

## Profiles & study sessions

A full-screen "Who's studying?" picker gates the app until a profile is chosen (see [Security model](#security-model) for what a profile is and isn't). The first profile created is offered the notes and conversations written before profiles existed; declining leaves them unowned, adoptable later from the same picker. Switching profiles (the title bar chip, or the phone shell's More menu) flushes any note still mid-autosave under the old profile, refuses while an answer is streaming, then reloads the page.

A saved conversation is a **study session** in the UI: rename, pin, search (by title or your own questions in it), and reopening one returns the reader to where it last ended (not just where it began) with a dismissible "Continuing…" banner. Find them from the toolbar's "Sessions" button, the phone shell's More menu, or the "History" button in the Ask panel header.

A session becomes **read-only** once it reaches `Sessions:MaxQuestions` or `Sessions:MaxContextTokens` (see [Configuration](#configuration)) — enforced server-side, not just by disabling the input. "Continue in a new session" writes a short recap (a separate, one-shot model call that never touches the full session's own history or, for Claude Code, its CLI session) and starts the next part with that recap folded into its first question; "Start fresh" begins an unrelated new session instead. A counter appears once either limit passes about half, and a notice once it's close.

## Mobile & tablet

`berean-web` is responsive: a tablet gets a two-pane shell (reader + tabbed
study panel, stacked in portrait and side by side in landscape), a phone
gets a bottom-nav single-view shell, and touch interactions (long-press
word lookup, resizable/draggable panes, the Android back button) are
handled throughout. Desktop is unchanged. The Android back button and
Escape close one overlay/sheet at a time rather than a full nested
history (see `back-stack.service.ts`).

Remote access from a phone/tablet over HTTPS — needed to install it as an
app — requires `tailscale serve` or an equivalent reverse proxy in front
of the three services, routing `/resource` and `/agent` to the two APIs
and everything else to the web app; `environments/*.ts` already switches
to origin-relative API URLs whenever the page is loaded over `https:`.

## Security model

Berean has **no authentication or authorization** anywhere: any request that reaches an API is served. Run it on `localhost` or on a trusted private network only, behind your own reverse proxy and auth if you need to expose it further. CORS is restricted to the origins in `Cors:AllowedOrigins`, but that only stops browsers from other sites from calling in on a victim's behalf — it isn't a substitute for real access control.

Berean does support more than one person: a "Who's studying?" **profile** picker keeps each person's notes and saved conversations ("study sessions") apart. This is **separation, not access control** — a profile is a name and a server-generated id, no password. `GET /api/profiles` lists every profile, and anyone who can reach the APIs can send any profile's id in the `X-Berean-Profile` header (REST) or `?profile=` (the SignalR hub) and read or write that profile's data, with `curl` or browser dev tools, without ever touching the picker. That's an acceptable tradeoff on a trusted private network among people who already share the household — it stops the two of you from overwriting each other's notes in *normal use*, nothing more. If Berean is ever exposed past a trusted network, real authentication (the profile id coming from an authenticated session instead of a header) becomes necessary.

## Deployment

For running the three apps continuously on a Windows machine, instead of `dotnet run`/`npm start` in a terminal:

- **`BereanResource.Api` and the built `berean-web`**: host both in IIS as ordinary sites (in-process ASP.NET Core hosting for the API; static files + URL Rewrite fallback to `index.html` for the web app — see [deploy/web/web.config](deploy/web/web.config)). [deploy/finish-iis-setup.ps1](deploy/finish-iis-setup.ps1) creates/starts the sites and app pools and checks that each one answers. Run it elevated.
- **`Berean.Agent.Api`**: **not** IIS. With the default `ClaudeCode` provider, the agent shells out to the `claude` CLI, which reads your Claude login from your Windows user profile. An IIS app pool's worker process — even set to run as your account — is a *batch logon*, and the CLI's stored login isn't visible there ("Not logged in" at runtime even though the account is right). A Windows Service running as your account gets a normal profile environment, so the CLI sees the login. [Program.cs](Berean.Agent.Api/Program.cs) calls `UseWindowsService` (a no-op under `dotnet run` or IIS); [deploy/install-agent-service.ps1](deploy/install-agent-service.ps1) retires the IIS site if one exists, copies a new build in, grants your account "Log on as a service", and installs/starts the `BereanAgent` service (`appsettings.json`'s `Urls` controls the port; default `http://*:5050`). Run it elevated, with the account you use for Claude Code.
- Redeploying the Agent after a code change: `dotnet publish Berean.Agent.Api -c Release -o <folder>`, then re-run `install-agent-service.ps1 -PublishDir <folder>` elevated (it stops the service first, copies files, restarts it).
- If you switch the Agent to the `Anthropic` or `OpenAI` provider instead, this constraint goes away — those authenticate with `ANTHROPIC_API_KEY`/`OPENAI_API_KEY` regardless of logon type, so IIS works fine for the Agent too.

## Evaluating changes

```bash
dotnet run --project eval/Berean.Eval -- --label baseline                  # uses the configured model
dotnet run --project eval/Berean.Eval -- --label check --retrieval-only    # no model: measures retrieval only
```

`eval/questions.json` holds 20 questions (contested doctrine, word studies, verse exegesis, a perspective group) with a rubric each. A run writes `eval/runs/<date>-<label>/` with every answer, exactly what the model was given, and a summary: the share of sources by tradition, the largest number any one module supplied, whether a held-out perspective's sources leaked into the neutral pass, and whether the citations in each answer point at sources that were really in the prompt. Only runs named `*-baseline` are committed.

Options: `--only id1,id2`, `--provider`, `--model`, `--mode compare`, `--db <index copy>`, `--api <url>`, `--index`.

## Tests

```bash
dotnet test Berean.Core.Tests
```

The Claude Code client is tested against a fake `claude` executable (built with the tests), so no subscription is used.

## Hub reference

The hub URL carries `?profile=<id>` (browsers can't set headers on a WebSocket), checked once in `OnConnectedAsync` against `BereanResource.Api`; a missing or unknown profile gets `Error` and the connection is closed.

Client → server: `StartConversation(modelId?, perspectives?)` (locked for the conversation's lifetime), `ResumeConversation(id)`, `ListConversations(query?)` (pinned first, then most recent; query matches the title or the profile's own questions), `DeleteConversation(id)`, `RenameConversation(id, title)`, `SetPinned(id, pinned)`, `SendMessage(text, mode, location?)` (`location` is `{moduleId,book,chapter,verse}` or null; refused once the session is full), `ContinueConversation()` (ends a full session and starts the next part, recap carried over), `SetLanguage`, `GetRagStatus`, `ReindexDocuments`.

Server → client, for one answer in order: `TokenReceived("")`, `Sources(list)`, `ToolActivity(name, text)`, `TokenReceived(chunk)`…, `MessageComplete(text)`. Also `SessionStarted`, `ConversationStarted`, `ConversationLoaded` (now includes `lastLocation`, `pinned`), `ConversationList`, `ConversationDeleted`, `SessionLimit(state)` (sent after `StartConversation`/`ResumeConversation`/`MessageComplete`/`ContinueConversation`: `{questionsUsed,maxQuestions,contextTokens,maxContextTokens,state}`, `state` one of `ok`/`nearing`/`full`), `ConversationContinued(previousId, previousTitle, recap)`, `RagStatus`, `RagIndexing`, `RagIndexed`, `Error`.

REST: `Berean.Agent.Api`: `GET /health`, `GET /api/models`, `GET /api/conversations/unowned`, `POST /api/profiles/{id}/adopt-unowned-conversations`. `BereanResource.Api`: `/api/bible` (including `/{module}/strongs/{number}/occurrences`), `/api/books`, `/api/commentary`, `/api/crossreferences`, `/api/dictionary` (including `/transliteration`), `/api/resources` (including `/profiles/unclassified`), `/api/profiles` (`GET`/`POST`, `GET/PUT /{id}`, `GET /unowned`, `POST /{id}/adopt-unowned`), `/api/notes` (including `POST /{reference}/append`) — every notes endpoint requires an `X-Berean-Profile: <id>` header naming an existing profile, and returns `400` without one; the other endpoints ignore it. Swagger UI in Development.

## Known limits

- The library is mostly 17th–19th-century Protestant commentary, so "balanced" is limited by what is in it. Catholic, Orthodox, Jewish and modern critical sources would help most (add the module, label it in `ModuleProfiles`).
- The Anthropic and OpenAI providers are covered by unit tests but have not been run against the live APIs.
- Small local models (via Ollama) follow the answering method poorly.

## License & content

The code in this repository is [MIT licensed](LICENSE). That covers the app only: Berean doesn't bundle any Bible module files. You point it at your own e-Sword/MySword modules (see [Configuration](#configuration)), and those modules keep whatever license they were distributed under — check before redistributing anything you export from them.
