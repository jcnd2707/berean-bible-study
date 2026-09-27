# Berean

[![CI](https://github.com/jcnd2707/berean-bible-study/actions/workflows/ci.yml/badge.svg)](https://github.com/jcnd2707/berean-bible-study/actions/workflows/ci.yml)

A Bible study app with an AI study assistant. The assistant answers from your own Bible study modules (Bibles, commentaries, dictionaries, prose books) and is built to be **even-handed**: it shows what the text says first, then how different traditions have read it, and it only brings in Adventist material when you turn that on.

> Imported from a private repository; the app itself, `Berean`, hasn't changed — only where the code lives.

## Solution layout

| Project | Type | Purpose |
|---|---|---|
| `Berean.Core/` | .NET 8 class library | The study agent: query routing, retrieval, tradition-aware indexing, prompts, tools, and the model providers |
| `Berean.Agent.Api/` | ASP.NET Core 8 | SignalR hub (`/hubs/chat`): conversations, streaming answers, sources, saved chats |
| `BereanResource.Api/` | ASP.NET Core 8 Web API | REST access to your Bible study modules: Bible, commentary, dictionary, cross-references, books, notes, Strong's occurrences, module profiles |
| `berean-web/` | Angular 19 + Tailwind 4 | Web client: Bible reader, commentary, dictionary, cross-references, notes, compare, AI chat |
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
2. **Retrieve, in two passes.**
   - *Main pass (neutral):* the exact verse text and every commentary's entry for the verse, read straight from the Resource API (so each tradition counts equally); plus semantic search of the vector index, excluding Adventist sources and capping how much any one module can add (`MaxPerModule`, default 2). Dictionary and lexicon entries are looked up for the words asked about.
   - *Adventist pass:* only when the SDA toggle is on. The same question restricted to Adventist modules, returned as a separate `ADVENTIST SOURCES` block.
3. **Label.** Every source is numbered and tagged with its tradition and era, e.g. `[S1] Barnes' Notes on the Bible (Evangelical, 19th c.) — John 3:16`. Adventist sources are `[A1]…`.
4. **Answer.** The model gets the material *before* the question. The system prompt ([system.md](Berean.Core/Agent/Prompts/system.md)) is about method: text first, then the main views at their strongest with who holds each, keeping what the text states apart from inference and dispute, never treating one tradition as "the" view, citing only sources it was given.
5. **Modes.** *Quick* skips retrieval. *Deep* retrieves. *Compare* sets the interpretive traditions side by side in a fixed structure.

The web client turns `[S1]`-style citations into chips that open the commentary or book chapter, lists the sources under each answer grouped by tradition, and can save an answer to the note for the passage you are reading.

### Tradition labels

Each module's tradition comes from `ModuleProfiles` in `BereanResource.Api/appsettings.json`:

```json
"barnes": { "Tradition": "Evangelical", "Era": "19th c.", "DisplayName": "Barnes' Notes on the Bible" }
```

Traditions: `Adventist`, `Reformed`, `Wesleyan`, `Baptist`, `Lutheran`, `Evangelical`, `Catholic`, `Orthodox`, `Jewish`, `Academic`, `Lexical`, `Unclassified`. `GET /api/resources/profiles/unclassified` lists modules that still need a label. Tags are stored on every chunk in the index; changing a label is applied to the index at the next start (no re-embedding).

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
- Bible study modules in a folder with `Bibles`, `Commentaries`, `Dictionaries`, `Lexicons` and `TopicNotes` subfolders (and `Books`): scrollmapper `.db` or MySword `.bbl` Bibles, MySword `.cmt` commentaries, MySword `.dct` dictionaries, and e-Sword `.lexi`/`.lexh` lexicons
- For the default provider: the Claude Code CLI, logged in (`claude` on your PATH)

## Configuration

Committed `appsettings.json` files hold placeholders; put machine-specific values in `appsettings.Development.json` (or user-secrets). API keys are only ever read from environment variables.

**`BereanResource.Api`**: `BereanResources:RootPath`, `NotesDbPath`, `CrossReferencesDbFolder`; `ModuleProfiles`; `Cors:AllowedOrigins`.

**`Berean.Agent.Api`**
- `Llm`: `Provider`, `Model`, `Models` (the ones the UI offers), `Effort`, `HistoryTurns`, and `ClaudeCode` (`ExecutablePath`, `WorkingDirectory`, `TimeoutSeconds`, `Effort`). On Windows an npm install of Claude Code puts a `claude.cmd` shim on the PATH; the real `claude.exe` beside it is used automatically.
- `Ollama`: `Endpoint`, `EmbeddingModel`, `DefaultModel`, `ToolCompatibleModels`.
- `Agents:BibleAgent`: `RagDbPath`, `ResourceApiBaseUrl` (required), `AllowedBibleModules`, `AllowedCommentaryModules` (empty = all), `MaxPerModule`, `AdventistTopK`, `TopK`, `MaxContextTokens`, `AutoIndexMissingModules`, chunk sizes.

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

Indexing missing modules is **off by default** (`AutoIndexMissingModules: false`) because embedding runs at only a few chunks a second on CPU and a full commentary is hours of work. Verse questions don't need it: they read every commentary directly from the API. Semantic search over a commentary needs it indexed. Turn the setting on, or send `ReindexDocuments` from the hub, to build what's missing; nothing already indexed is touched, and an interrupted module is redone from scratch.

## Security model

Berean has **no authentication or authorization** anywhere: any request that reaches an API is served. There's no multi-user support — notes, saved conversations and RAG state are shared by whoever can reach the app. Run it on `localhost` or on a trusted private network only, behind your own reverse proxy and auth if you need to expose it further. CORS is restricted to the origins in `Cors:AllowedOrigins`, but that only stops browsers from other sites from calling in on a victim's behalf — it isn't a substitute for real access control.

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

`eval/questions.json` holds 20 questions (contested doctrine, word studies, verse exegesis, SDA on) with a rubric each. A run writes `eval/runs/<date>-<label>/` with every answer, exactly what the model was given, and a summary: the share of sources by tradition, the largest number any one module supplied, whether Adventist sources leaked with the toggle off, and whether the citations in each answer point at sources that were really in the prompt. Only runs named `*-baseline` are committed.

Options: `--only id1,id2`, `--provider`, `--model`, `--mode compare`, `--db <index copy>`, `--api <url>`, `--index`.

## Tests

```bash
dotnet test Berean.Core.Tests
```

The Claude Code client is tested against a fake `claude` executable (built with the tests), so no subscription is used.

## Hub reference

Client → server: `StartConversation(modelId?)`, `ResumeConversation(id)`, `ListConversations()`, `DeleteConversation(id)`, `SendMessage(text, mode, includeSDA)`, `SetLanguage`, `ResetConversation`, `GetRagStatus`, `ReindexDocuments`.

Server → client, for one answer in order: `TokenReceived("")`, `Sources(list)`, `ToolActivity(name, text)`, `TokenReceived(chunk)`…, `MessageComplete(text)`. Also `SessionStarted`, `ConversationStarted`, `ConversationLoaded`, `ConversationList`, `ConversationDeleted`, `RagStatus`, `RagIndexing`, `RagIndexed`, `Error`.

REST: `Berean.Agent.Api`: `GET /health`, `GET /api/models`. `BereanResource.Api`: `/api/bible` (including `/{module}/strongs/{number}/occurrences`), `/api/books`, `/api/commentary`, `/api/crossreferences`, `/api/dictionary` (including `/transliteration`), `/api/notes` (including `POST /{reference}/append`), `/api/resources` (including `/profiles/unclassified`). Swagger UI in Development.

## Known limits

- The library is mostly 17th–19th-century Protestant commentary, so "balanced" is limited by what is in it. Catholic, Orthodox, Jewish and modern critical sources would help most (add the module, label it in `ModuleProfiles`).
- The Anthropic and OpenAI providers are covered by unit tests but have not been run against the live APIs.
- Small local models (via Ollama) follow the answering method poorly.
