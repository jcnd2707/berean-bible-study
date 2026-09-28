# Plan: per-person profiles, separate notes, and study sessions

Two people now use one Berean install. This plan gives each person their own **notes** and their
own **chat history**, and turns that history into **study sessions** you can find and reopen.
Both features share one change underneath (knowing *who* is using the app), so they're planned and
shipped together. Because sessions can now be reopened and continued, each session also gets a
**length limit** (Phase 5), so the assistant stays focused and no single chat goes on forever.

---

## 1. What the app does today

### Notes
- `BereanResource.Api` → `NotesService` stores notes in `notes.db` (path from
  `BereanResources:NotesDbPath`). One table, **one note per reference**:
  `Notes(Reference TEXT PRIMARY KEY, Text, CreatedAt, UpdatedAt)`.
- REST: `GET/POST/DELETE /api/notes/{reference}`, `POST /api/notes/{reference}/append`
  ([NotesController.cs](BereanResource.Api/Controllers/NotesController.cs)).
- Web: `NotesComponent` (auto-saves the note for the current verse/chapter after 1s),
  `NotesListComponent` ("My Notes" browser), the chat's "Save to notes" (appends an answer), and
  the "has a note" markers in the reader (`nav.setNotedReferences`).
- **Nothing identifies the user**, so there is exactly one note per verse for everyone. If you
  both write on John 3:16 you're editing the *same* note, and the 1s auto-save means the last
  person to type wins.

### Chat history — already exists, but hidden and shared
This part is already mostly built. It's just hard to find:
- `Berean.Agent.Api` → `ConversationStore` saves every conversation to `chat.db` (next to
  `bible.rag.db`): `Conversations(Id, Title, CreatedAt, UpdatedAt, Passage, ModelId,
  ClaudeSessionId, Perspectives)` + `Messages(...)`, including tool calls, so a resumed
  conversation keeps its full context (and resumes the Claude Code CLI session).
- Hub methods `ListConversations`, `ResumeConversation`, `DeleteConversation` all work. The
  browser remembers the current conversation in `localStorage["berean_conversationId"]` and
  resumes it after a refresh.
- Web: the **12 px clock icon** in the Ask panel header toggles a "Saved conversations" list.
  That icon is the only way in, which is why it's easy to miss.

Problems for two people:
1. **Shared:** both of you see, reopen and can delete each other's conversations.
2. **Hard to find:** a small icon with no label, and the list only exists inside the chat panel.
3. **Missing basics:** no rename (`ConversationStore.RenameAsync` exists but no hub method calls
   it), no search, no pinning. Delete happens straight away with no confirmation. The passage is
   only a display string ("John chapter 3"), so reopening a session can't take the reader back
   to where you were.
4. **Shared device:** `berean_conversationId` is one key per browser, so on a shared PC one
   person's reload resumes the other person's last chat.

### How long a conversation can get today
- `Llm:HistoryTurns` (12) is a **silent sliding window** (`StudyAgent.TrimHistory`). Past 12
  questions, the oldest exchanges are dropped (it cuts back to 9) and the model simply forgets
  them. The UI still shows them, so you can't tell what the assistant no longer knows.
- The app's own history is kept small: after each turn, the retrieved material is removed and
  only the plain question is kept (`StudyAgent.cs:134`). **But with the default Claude Code
  provider**, the CLI session is resumed with `--resume`, and *that* session keeps the full text
  of every turn, including all the retrieved commentary. So the context the model actually sees
  grows by thousands of tokens per Deep/Compare question. The only thing that resets it today is
  the trim above, which forces a new CLI session with a condensed transcript
  (`TranscriptBuilder`).
- The real context size is already measured: every turn logs `[Usage] … in=N`, and for Claude Code
  `InputTokenCount` includes cache reads and writes (`ClaudeCodeChatClient.cs:338`). It just isn't
  stored or acted on.
- Nothing caps a conversation's length, so reopening sessions (this plan) would make an endless
  chat easy.

### Bugs found along the way
- `ChatHub.ResetConversation()` calls `StartConversation(null)`. It drops the model and
  perspective you picked, so after **New conversation** the dropdown still shows e.g. *sonnet*
  while the server answers with the default model, and the saved `ModelId` is null.
  This is fixed in Phase 4.
- Conversation delete has no confirmation. Also fixed in Phase 4.

---

## 2. Design decisions

### D1. Identity: profiles, not logins (recommended)
A "Who's studying?" profile picker. Each browser remembers the profile you chose, and every API
call carries it. **No passwords.**

Why: the README's security model is already "trusted private network only, no auth anywhere",
and the Tailscale/LAN setup keeps it that way. A password screen would *look* like security
without providing it, because any client on the network could still send any profile id. What
you need is to **keep your data apart and not overwrite each other**, not protection from each
other. Profiles give you exactly that with the least code.

Alternatives considered:
- *Real accounts (ASP.NET Identity, cookies, passwords):* a lot of code across two APIs plus
  SignalR auth, for a threat that doesn't exist here. Revisit only if the app is ever exposed
  beyond the tailnet.
- *Tailscale identity:* `tailscale serve` adds `Tailscale-User-Login` headers, so the profile
  could be picked automatically. But plain-HTTP LAN access bypasses Serve, and it only works if
  you each sign in to Tailscale with separate accounts. It could be a later add-on that
  **pre-selects** the profile, not the core mechanism.

> **Decided (2026-09-28):** no PIN. Profiles are a name picker only.

**This separation is cooperative, not access control, and that's a deliberate tradeoff.**
Profiles stop the two of you from overwriting each other's notes and from mixing your chat
histories *in normal use of the app*. They do **not** stop anyone from reading or changing another
profile's data:
- `GET /api/profiles` lists every profile, including ids, by design, because the picker needs it.
- Any REST client can send any `X-Berean-Profile` header, and any hub client can connect with
  any `?profile=`. Nothing checks that the caller *is* that person.

So anyone who can reach the APIs (anyone on the tailnet or LAN, with a browser's dev tools or
`curl`) can read and write any profile's notes and study sessions without ever touching the
picker. This is the same trust level as the rest of Berean, where every API is already open to
anyone on the network (README, *Security model*). It's acceptable because the only people on
that network are the household. If that ever changes, the answer is real authentication (see
*Out of scope*), not a PIN. The README's *Security model* section must say this in so many words
(§6).

### D2. Where profiles live
In `BereanResource.Api`, as a `Profiles` table in `notes.db`. That database is already the
"personal data" store, and the Resource API is the one every client talks to over REST.
The Agent API doesn't need its own copy. It treats the profile id as an opaque key and checks
once per connection that the id exists (via `GET /api/profiles/{id}`, using the Resource API
URL it already has in `Agents:BibleAgent:ResourceApiBaseUrl`).

Profile ids are server-generated GUIDs (`"N"` format), so renaming a profile never has to touch
notes or conversations.

### D3. How the profile travels
| Channel | Mechanism |
|---|---|
| REST → Resource API | `X-Berean-Profile: <id>` header, added by an Angular HTTP interceptor (CORS already `AllowAnyHeader`) |
| SignalR → Agent API | `?profile=<id>` on the hub URL (browsers can't set headers on WebSockets); read once in `OnConnectedAsync` |

Endpoints that hold personal data **require** the profile and return `400` without it. Bible,
commentary, dictionary and other module endpoints ignore it.

### D4. Switching profile = flush, then full page reload
Switching ends in `location.reload()`, which resets everything in memory at once: the hub
connection (reconnects with the new `?profile=`), the chat messages, the notes panel and the
noted-verse markers. No per-profile state can leak across a switch, and nothing has to be torn
down by hand.

But a reload doesn't run Angular's `ngOnDestroy`, and it cancels in-flight requests. So a note
typed less than a second before switching (still in `NotesComponent`'s 1s debounce) would be
lost. `ProfileService.switchTo(id)` therefore runs **in this order**:
1. **Refuse while an answer is streaming.** Show "Wait for the answer to finish". The server only
   saves completed turns, so a disconnect mid-answer would lose it.
2. **Flush pending saves under the *old* profile, and await them.** `NotesComponent` registers a
   flush callback with a small `PendingSavesService` (`register(fn: () => Promise<void>)`,
   `flushAll()`). The callback saves `noteText` if `isDirty()`. The order matters: the
   interceptor reads the current profile when a request is sent, so flushing *after* changing the
   stored id would save your note into the other person's profile.
3. Write `localStorage["berean_profileId"]`, then `location.reload()`.

The same gap already exists today for closing the tab or app (the `ngOnDestroy` "backstop" in
`NotesComponent` doesn't run on unload either). Fix it at the same time: a `pagehide` listener
that sends a dirty note with `fetch(…, { keepalive: true })` and the profile header set
explicitly.

### D5. A "study session" *is* a saved conversation, enriched
I considered a separate `StudySession` entity grouping several chats and notes, and rejected it:
more schema, more UI, and it duplicates what a conversation already is. Instead, conversations
get what makes them useful to come back to: a **structured last location** (so reopening takes
the reader back), **rename**, **pin**, **search**, and a **proper place in the UI**. In the UI
they're called *Study sessions*. In code they stay `Conversation`.

### D6. Existing data
Everything written before profiles is **unowned** (`ProfileId = ''` for notes, `NULL` for
conversations). When the first profile is created, the picker asks *"Keep the notes and study
sessions made before profiles? (N notes, M sessions)"*, and on **yes** they're assigned to that
profile. Nothing is ever silently given to the wrong person, and both databases are backed up
before migrating.

### D7. Sessions have a visible, hard length limit, and "continue" carries a recap
A session is **full** when it reaches **either** limit:
- a **question count** (`Sessions:MaxQuestions`, starting at **12**), which is predictable and
  easy to show as "7 of 12"; or
- a **context size** (`Sessions:MaxContextTokens`, starting at **60,000**), the highest per-turn
  input-token figure reached anywhere in the session. This is the guard that matters for Claude Code, where a few
  Compare questions with lots of commentary can fill the context well before 12 questions.

When a session is full, it becomes **read-only**. You can still read it, open citations and "Save
to notes", but not ask anything more. The UI offers **Continue in a new session**. That generates
a short **recap** of the full session (passages studied, conclusions reached, open questions),
starts a new session with the same model and perspective, and gives the recap to the model with
the first question. The two sessions are linked ("Continued from…" / "Continued in…").

Why this design:
- **Hard, not advisory.** A warning you can click past doesn't prevent the endless chat. It's
  enforced on the server (`SendMessage` refuses when full), not just by a disabled input.
- **No silent forgetting inside a session.** `HistoryTurns` must be ≥ `MaxQuestions` (checked at
  startup), so within one session the model sees everything you see. It also stops the
  mid-session Claude Code restarts the trim causes today. The trim stays as a safety net.
- **The recap replaces the transcript.** Continuing starts the model from a focused summary
  rather than the whole previous conversation, which is exactly what keeps it effective.
- **The model writes the recap** (one extra call, prompt in `Prompts/recap.md` per the
  prompts-are-markdown convention), and it's stored on the old session so the sessions list
  can show it as a preview. If that call fails, the fallback is `TranscriptBuilder.Condense`,
  which needs no model call.

> **Decided (2026-09-28):** start at 12 questions / 60k tokens. They're config values: after a
> week of use, the `[Usage] in=` log lines will show how quickly Deep and Compare questions
> actually grow the context, and the limits can be tuned from that.

### What stays shared
The Bible modules, the vector index, model/perspective lists, and per-device preferences
(font size, reader theme, tablet pane split). Font and theme belong to the device, not the
person. Moving them to per-profile later is a small change.

Also note: both of you draw on **the same Claude subscription** (the operator's login that the
`BereanAgent` service runs as), so usage limits are shared.

---

## 3. Data model changes

### `notes.db` (Resource API)
```sql
CREATE TABLE IF NOT EXISTS Profiles (
    Id        TEXT PRIMARY KEY,      -- Guid "N"
    Name      TEXT NOT NULL,
    Color     TEXT,                  -- avatar accent, optional
    CreatedAt TEXT NOT NULL
);

-- Notes: the primary key becomes (ProfileId, Reference). SQLite can't alter a primary key, so
-- this is a one-time table rebuild, run only when Notes has no ProfileId column:
--   1. copy notes.db → notes.db.pre-profiles.bak   (once; skip if the .bak exists)
--   2. in one transaction:
CREATE TABLE Notes_new (
    ProfileId TEXT NOT NULL,
    Reference TEXT NOT NULL,
    Text      TEXT NOT NULL DEFAULT '',
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY (ProfileId, Reference)
);
INSERT INTO Notes_new SELECT '', Reference, Text, CreatedAt, UpdatedAt FROM Notes;
DROP TABLE Notes;
ALTER TABLE Notes_new RENAME TO Notes;
```
The Strong's occurrence cache that sits next to `notes.db` is unaffected.

### `chat.db` (Agent API)
Additive only, so it can use the existing `AddColumnIfMissing` helper (back up to
`chat.db.pre-profiles.bak` first anyway):
```sql
ALTER TABLE Conversations ADD COLUMN ProfileId    TEXT;     -- NULL = unowned (pre-profiles)
ALTER TABLE Conversations ADD COLUMN LastLocation TEXT;     -- JSON {moduleId,book,chapter,verse}
ALTER TABLE Conversations ADD COLUMN Pinned       INTEGER NOT NULL DEFAULT 0;
-- Phase 5 (length limit):
ALTER TABLE Conversations ADD COLUMN QuestionCount    INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Conversations ADD COLUMN ContextTokens    INTEGER;  -- highest per-turn input tokens reached
ALTER TABLE Conversations ADD COLUMN ContinuedFromId  TEXT;     -- previous part of this study
ALTER TABLE Conversations ADD COLUMN Recap            TEXT;     -- written when the session is continued
-- Backfill QuestionCount for existing conversations once, from Messages (Role = 'user').
CREATE INDEX IF NOT EXISTS IX_Conversations_Profile ON Conversations (ProfileId, Pinned, UpdatedAt);
```

---

## 4. Implementation phases

Phases 1–3 are **one release**. Don't deploy profiles without the separation behind them, or the
second person gets a picker that doesn't keep anything apart. Phases 4 and 5 are the second
release, together, because reopening sessions without a length limit is what invites the
endless chat.

### Phase 1: Profiles (identity plumbing)

**Resource API**
- `Services/ProfileService.cs`: `EnsureCreated`, `List`, `Get`, `Create(name)`, `Rename`,
  `Exists` (with a small in-memory cache, since it's called on every notes request).
- `Controllers/ProfilesController.cs`:
  - `GET /api/profiles`, `GET /api/profiles/{id}`, `POST /api/profiles {name}`,
    `PUT /api/profiles/{id} {name}`
  - `GET /api/profiles/unowned` → `{ notes: n }`
  - `POST /api/profiles/{id}/adopt-unowned` → moves `ProfileId=''` notes to `{id}`
    (`UPDATE OR IGNORE`, so a clash can't lose data; any leftover rows stay unowned and are
    reported).
- `Profiles/RequireProfileAttribute.cs`: an action filter that reads `X-Berean-Profile`, checks
  `ProfileService.Exists`, stores the id in `HttpContext.Items`, and returns `400 { error: "No
  profile selected." }` otherwise. Plus a `HttpContext.ProfileId()` extension.
- `Program.cs`: register `ProfileService`, and call its `EnsureCreated()` next to
  `NotesService.EnsureCreated()`.
- No delete in v1. Deleting a profile would have to reach into `chat.db` too. Out of scope (§7).

**Agent API**
- `ChatHub.OnConnectedAsync`: read `Context.GetHttpContext()?.Request.Query["profile"]`, check the
  format (`^[0-9a-f]{32}$`) and that it exists (one Resource API call), then
  `_sessions.SetProfile(connectionId, id)`. On failure, send `Error("Choose a profile first.")`
  and `Context.Abort()`.
- `StudySessionService`: a `_profiles` `ConcurrentDictionary` alongside `_languages`, cleared in
  `RemoveSession`. Add `GetProfile(connectionId)`, which throws if unset.
- Add a `GetProfileAsync(id)` to `BereanResourceApiClient` in `Berean.Core/Resources` (it
  already talks to the Resource API).

**Web**
- `core/services/profile.service.ts`: a `current` signal, `list()`, `create()`, `rename()`,
  `switchTo(id)` (localStorage + reload), and a `ready` promise like `ModelService.ready`.
  Stores `berean_profileId`.
- `core/interceptors/profile.interceptor.ts`: a functional interceptor that adds
  `X-Berean-Profile` **only** to requests whose URL starts with `environment.apiBaseUrl`.
  Register it with `provideHttpClient(withFetch(), withInterceptors([profileInterceptor]))` in
  `app.config.ts`.
- `features/profiles/profile-picker.component.ts`: a full-screen "Who's studying?" page (big
  name tiles, "+ Add person"). The first profile created shows the adopt-unowned checkbox (§2 D6),
  which calls both APIs' adopt endpoints.
- `app.component.ts`: show the picker instead of the shell until a valid profile is selected.
  If the stored id no longer exists (`GET /api/profiles/{id}` returns 404), clear it and show the
  picker.
- `agent-hub.service.ts`: build the hub URL with `?profile=` from `ProfileService`. The
  connection is built in the constructor, so either inject `ProfileService` and read it there
  (it's set before any shell renders) or build lazily in `connect()`.
- Profile switcher: an avatar/name chip in `title-bar.component.ts` (desktop/tablet) and a
  "Switch person" item in the phone shell's **More** menu. Both call `switchTo`, which does the
  refuse-while-streaming → flush → reload sequence in D4.
- `core/services/pending-saves.service.ts` (D4), with `NotesComponent` registering its flush,
  plus the `pagehide` keepalive save.

### Phase 2: Separate notes

**Resource API**
- `NotesService`: a `MigrateToProfiles()` step inside `EnsureCreated` (backup + rebuild, §3). Give
  every method a `profileId` parameter and add `ProfileId = $p` to every query (`Get`, `GetAll`,
  `Upsert`, where the `ON CONFLICT` target becomes `(ProfileId, Reference)`, `Append`, `Delete`),
  plus `CountUnowned()` / `AdoptUnowned(profileId)`.
- `NotesController`: `[RequireProfile]` on the controller, and pass `HttpContext.ProfileId()`
  through.

**Web**
- No changes to the notes components. The interceptor covers `NotesService`, the chat's "Save to
  notes", the `ngOnDestroy` backstop save and the noted-verse markers. That's the point of
  sending the profile as a header.

### Phase 3: Separate chat history

**Agent API**
- `ConversationStore`:
  - `EnsureCreated`: back up, then `AddColumnIfMissing` for `ProfileId`, `LastLocation`,
    `Pinned`, plus the new index.
  - `ConversationInfo` gets `ProfileId`, `LastLocation`, `Pinned`.
  - `ListAsync(profileId, …)`: `WHERE ProfileId = $p`.
  - `GetAsync(id, profileId)`: `WHERE Id = $id AND ProfileId = $p`. **Resume goes through this**,
    so a stale or foreign id behaves like "not found", and the hub already falls back to a fresh
    conversation in that case.
  - `DeleteAsync(id, profileId)`: guard **both** statements, i.e.
    `DELETE FROM Messages WHERE ConversationId IN (SELECT Id FROM Conversations WHERE Id=$id AND ProfileId=$p)`
    and then the conversation. The current version deletes messages by id alone.
  - `CountUnownedAsync()`, `AdoptUnownedAsync(profileId)`.
- `StudySessionService`: stamp `ProfileId` in `SaveTurnAsync` → `CreateAsync`, and pass
  `GetProfile(connectionId)` through `ResumeConversationAsync`, `ListConversationsAsync` and
  `DeleteConversationAsync`. The hub methods need the connection id for this, which they have.
- REST for the picker's adopt step: `GET /api/conversations/unowned` → `{ count }` and
  `POST /api/profiles/{id}/adopt-unowned-conversations`.

**Web**
- `ai-chat.component.ts`: make `CONVERSATION_KEY` per profile (`berean_conversationId:<profileId>`),
  so a shared PC resumes the right person's last session.

### Phase 4: Study sessions (the "come back to it" part)

**Agent API**
- Structured location: `SendMessage(text, mode, location)`, where `location` is `{ moduleId,
  book, chapter, verse }` or null. `SaveTurnAsync` writes it to `LastLocation` on every turn, so
  it tracks where the study **ended**, not just where it began. Keep `Passage` (from the first
  message) as the display subtitle. The client and server must ship together: SignalR matches
  argument counts.
- New hub methods: `RenameConversation(id, title)` (store method already exists),
  `SetPinned(id, pinned)`, and `ListConversations(query?)` (a `LIKE` over `Title` and
  user-message `Content`; plenty for two people's data. Move to FTS5 only if it ever feels slow).
  Sort order: pinned first, then `UpdatedAt DESC`. Every method goes through the same
  profile-scoped store calls.
- `ConversationLoaded` and `ConversationDto` gain `lastLocation` and `pinned` (camelCase, per the
  convention).
- Fix the `ResetConversation` bug: take `(modelId, perspectives)` like `StartConversation`, or
  have the client call `startConversation(...)` directly and drop `ResetConversation`.

**Web**
- `features/sessions/sessions-list.component.ts`: a **Study sessions** overlay, modelled on
  `NotesListComponent`, with:
  - search box, a *Pinned* section, then *Today / This week / Earlier*
  - each row: title, passage, date, model; actions **Open**, **Rename** (inline), **Pin**,
    **Delete** (with confirmation)
- Entry points: a **"Sessions"** button next to **My Notes** in `toolbar.component.ts`, a
  **Sessions** item in the phone **More** menu, and in the Ask header replace the bare clock
  icon with a labelled "History" button that opens the same overlay. The inline
  `history-list` in `ai-chat.component.html` goes away, so there's one list, not two.
  Open/close state lives in `NavigationStateService` like `showNotesList`, so it joins the
  back-button stack (`back-stack.service.ts`).
- Opening a session **from the list** navigates the reader to `lastLocation` and switches to the
  Ask panel/tab (via `nav.requestRightTab("ask")`). **Auto-resume after a reload** does *not*
  move the reader.
- A thin banner at the top of a reopened session: *"Continuing: <title> · last studied Mar 3 ·
  John 3:16"*.
- `ai-chat.component.ts` `send()`: pass `nav.location()` as the new third argument.
- Nice extra (optional): **"Sessions on this passage"** under the note in `NotesComponent`, a
  `ListConversations` filtered to the current book+chapter. It links your notes and chats
  without any new schema.

### Phase 5: Session length limit

**Config** (`Berean.Agent.Api/appsettings.json`, a new top-level section):
```json
"Sessions": {
  "MaxQuestions": 12,
  "WarnQuestionsLeft": 3,
  "MaxContextTokens": 60000,
  "WarnContextFraction": 0.8
}
```
It needs a `SessionLimits` options class, bound in `Program.cs`. At startup, if
`Llm:HistoryTurns < Sessions:MaxQuestions`, **raise `HistoryTurns` to `MaxQuestions`** and log a
warning. Never lower `MaxQuestions`. That's the direction that makes D7's "no silent forgetting
inside a session" guarantee hold: `TrimHistory` runs before the new question is added and trims
only when the count *exceeds* the limit, so with `HistoryTurns == MaxQuestions` the 12th
question still sees all 11 before it.

**Core**
- Context size per turn: add `ContextTokens` to `TurnRecord` (`StudyPipeline.cs:187`), set in
  `StudyAgent.ChatStreamAsync` to the **largest single request's** input tokens in that turn.
  Not the last one, and not the sum:
  - `updates.ToChatResponse().Usage` **sums** every tool round (Anthropic/OpenAI go through
    `UseFunctionInvocation`, `ChatClientFactory.cs:50`), which overstates the context. So don't
    use it.
  - The *last* request can understate: a small trailing request would hide a round that briefly
    ballooned. This feeds a hard limit, so measure the worst case the model actually held.
  - How: in the existing streaming loop, take the max `InputTokenCount` over each update's
    `UsageContent`, one per inner request. Claude Code makes one request per turn (no tools),
    so for the default provider max = last = the reported figure. A unit test with a fake
    client emitting two rounds pins down that the inner usage updates really come through
    streaming. If they don't, fall back to the estimate below.
  - Ollama may report nothing. Fall back to an estimate (characters of the composed request ÷ 4).
- The **session's** `ContextTokens` is the max over all its turns
  (`ContextTokens = MAX(ContextTokens, $turn)` in `AppendAsync`), which is monotonic and
  conservative. With Claude Code the context only grows within a session anyway. With API
  providers, where retrieved material is dropped after each turn, it can shrink, but a session
  that once held 60k is treated as full.
- Carry-over: `StudyAgent` gets a `CarryOver` string that `ComposeUserMessage` puts before the
  retrieved material **on the first turn only** ("[Recap of the earlier part of this study] …").
  Because it's part of that first user message, it's stored and replayed on resume like anything
  else. No fake assistant messages, and the system prompt stays unchanged (which keeps the prompt
  cache valid).
- `Prompts/recap.md` (an embedded resource like the others): write a recap of 150–250 words:
  passages and verses studied, what was concluded and from which sources/traditions, and open
  questions. Keep the tradition labels (the app's neutrality applies to recaps too). No new
  claims.
- `StudyPipeline.WriteRecapAsync()`: one model call over the condensed transcript
  (`TranscriptBuilder.Condense`, which already strips retrieved material) with `recap.md` as the
  system prompt. It must be a **one-shot call that no conversation bookkeeping can see**:
  - **Bypass `StudyAgent`.** Call `pipeline.Llm.Client.GetResponseAsync([system, user], options)`
    directly, with no tools. Going through `StudyAgent.ChatStreamAsync` would append the recap
    to `_history` and fire `TurnCompleted`. That would save it to `chat.db` as a turn and bump
    `QuestionCount`/`ContextTokens`.
  - **No CLI session at all (Claude Code).** Today `ClaudeCodeChatClient` keys CLI sessions by
    `ChatOptions.ConversationId`, falls back to a shared `"default"` key when there isn't one, and
    records every call in `_sessions`. Add an explicit one-shot option (e.g.
    `options.AdditionalProperties["berean.oneShot"] = true`). When it's set, the client:
    - omits `--session-id`/`--resume` and passes `--no-session-persistence` (confirmed in
      `claude --help`: "sessions will not be saved to disk and cannot be resumed (only works with
      --print)", and Berean always runs with `-p`)
    - never reads or writes `_sessions`

    That means there's no session id to collide with a real conversation, no session file left in
    the isolated working directory, and nothing that could get resumed later. Other providers are
    stateless per call, so the flag is a no-op for them.
  - **Visible in usage, invisible to chat.db.** Call `UsageTracker.Record` for it with a purpose
    label so the log reads `[Usage] … (recap)`. It still counts against the shared subscription,
    so it should be visible. In `chat.db` the only trace is the `Recap` text on the **old**
    conversation. No messages, no `ClaudeSessionId` change, and no count changes on either
    conversation. The new conversation's `ContinuedFromId` is set by `ContinueConversationAsync`,
    not by the recap call, so "continued from" can't be confused by it.
  - **Spike during implementation:** one manual run of the real CLI with `-p --output-format
    stream-json --no-session-persistence` (no `--session-id`), to confirm it still emits the
    `result` event with `usage` that `ClaudeCodeChatClient` parses. Then a FakeClaude test that
    asserts the argument list.

**Agent API**
- `ConversationStore`: `AppendAsync` increments `QuestionCount` and sets `ContextTokens`.
  `SetRecapAsync(id, recap)`. `CreateAsync` accepts `ContinuedFromId`. `GetAsync` returns
  these, and also "continued in" (the id of the session whose `ContinuedFromId` points here),
  queried on read.
- `StudySessionService`:
  - `LimitState(info)` → `{ questionsUsed, maxQuestions, contextTokens, maxContextTokens, state }`
    where `state` is `ok | nearing | full`. Full = `questionsUsed >= MaxQuestions` or
    `contextTokens >= MaxContextTokens`.
  - `ContinueConversationAsync(connectionId)`: load the current (full) session, write the recap
    (fallback: `Condense`), save it on the old session, start a new conversation with the same
    model and perspectives, `ContinuedFromId` = old id, title "<old title> (part 2)" (or
    part N+1), and pipeline `CarryOver` = recap. Profile-scoped like everything else.
- `ChatHub`:
  - `SendMessage`: before calling the model, if the session is full, send
    `SessionLimit(state)` and return. **This is the enforcement.**
  - After `MessageComplete`, and in `ConversationStarted` / `ConversationLoaded`, send
    `SessionLimit(state)` so the client always knows where it stands.
  - New `ContinueConversation()` → sends `ConversationStarted(newId)` plus
    `ConversationContinued(previousId, previousTitle, recap)`.
- Old conversations longer than the limit simply open as full (read-only) with the Continue
  button. No migration needed beyond the `QuestionCount` backfill.

**Web**
- `agent-hub.service.ts`: `sessionLimit$`, `conversationContinued$`, `continueConversation()`.
- `ai-chat.component.ts/html`:
  - A small counter by the input, "7 / 12", shown once any limit passes about half.
  - A **nearing** notice: "3 questions left in this session. Long sessions make the assistant
    less focused." If the context-token limit is the one that's close, say so in plain words
    ("This session is holding a lot of material").
  - **Full**: replace the input with a card: *"This session is complete."* and the buttons
    **Continue in a new session** (recap carried over) and **Start fresh** (no recap). Quick-asks
    are disabled.
  - A continued session shows a collapsible **"Continued from <title>"** banner with the recap,
    linking back to the previous part. A full session shows a **"Continued in <title>"** link
    if a later part exists.
- Sessions list (Phase 4): show "part 2" chains together (indent, or a "3 parts" badge on the
  latest), mark full sessions with a ✓ rather than hiding them, and use the recap as the row's
  preview text when one exists.

---

## 5. Tests

**`BereanResource.Api.Tests`** (runs the real API against a scratch `notes.db`)
- Profiles: create/list/rename; unknown id → 404.
- Notes isolation: A writes `Jhn.3.16`, B reads `Jhn.3.16` → 404; B writes its own; both
  survive; `GET /api/notes` for each lists only their own.
- Missing or unknown `X-Berean-Profile` on `/api/notes/*` → 400. Bible/commentary endpoints
  still work without it.
- Migration: seed a **pre-profiles** `notes.db` (old schema) → start the API → rows are unowned,
  the `.bak` exists, adopt moves them to the profile, a second start doesn't re-migrate.
- Update the existing `NotesTests` to send the header.

**`Berean.Core.Tests`** (`ConversationStoreTests`)
- `ListAsync` / `GetAsync` / `DeleteAsync` are profile-scoped, and a foreign delete removes **no
  messages**.
- An old-schema `chat.db` gains the columns, keeps its rows, and they're unowned.
- Adopt, rename, pin ordering, search, and the `LastLocation` round-trip.
- `SerializeSources` / DTOs stay camelCase (add `lastLocation`, `pinned`).
- Length limit: `QuestionCount` backfill on an old-schema db; `LimitState` at/under/over each
  limit, whichever is hit first; `ContinuedFromId` / "continued in" round-trip.

**`Berean.Core.Tests`** (agent, using the fake `claude`)
- `SendMessage` on a full session makes **no model call** (the fake `claude` records
  invocations).
- `CarryOver` appears in the first composed user message only, and is replayed after resume.
- The recap call: FakeClaude sees `--no-session-persistence` and **no** `--session-id` /
  `--resume`; `ClaudeCodeChatClient._sessions` is unchanged afterwards (the study's next message
  still `--resume`s its own session); no `TurnCompleted` fires; `QuestionCount`/`ContextTokens`
  of both conversations are unchanged. A failing recap falls back to `Condense`.
- `ContextTokens` for a multi-round tool turn (a fake client emitting rounds of 20k then 35k then
  8k input tokens) is **35k**: not 8k (last) and not 63k (sum).
- Startup raises `HistoryTurns` to `MaxQuestions` (and never lowers `MaxQuestions`). With both
  at 12, the 12th question's request still contains the 1st question.

**Manual (web)**
- Type in a note, switch profile within a second: the text is saved to the **original**
  profile, and the new profile doesn't have it.
- Switching while an answer streams is refused.
- Closing the tab straight after typing keeps the note (`pagehide` keepalive).

**`berean-web`** (`npm run test:unit`, esbuild + node)
- Pull the pure bits into testable functions: the per-profile storage key and the session date
  grouping (Today/This week/Earlier). Add them to the `test:unit` script like
  `layout-classification`.

**Manual check before deploying:** two browsers (or one normal + one private window) as two
profiles, on a **copy** of the live `notes.db`/`chat.db`. Per CLAUDE.md, don't test against
`D:\Bible Study\` directly. Check that notes, markers, "Save to notes", history, resume after
reload, and delete all stay separate, and that switching profile on one device swaps everything.

---

## 6. Rollout

1. **Back up** the live `notes.db` and `chat.db` by hand (the migrations back themselves up too,
   but take your own copy).
2. Build and test (`dotnet test`, `npm run test:unit`), then do the two-profile manual check on
   copies.
3. Deploy **all three together**. An old web client against the new Resource API gets 400 on
   notes, and a new client against the old Agent API won't send `location`.
   - Resource API + web → IIS as today (`deploy/finish-iis-setup.ps1` / copy the build).
   - Agent → `dotnet publish Berean.Agent.Api -c Release -o <folder>`, then
     `deploy/install-agent-service.ps1 -PublishDir <folder>` from an **elevated** PowerShell.
4. First launch: create your profile **first** and tick "keep existing notes and sessions".
   Then create your wife's profile.
5. When Phases 4–5 ship: any existing conversation already over 12 questions opens as
   **complete**, with "Continue in a new session". That's expected. After a week, look at the
   `[Usage] in=` lines in the Agent log and adjust `Sessions:MaxQuestions` /
   `MaxContextTokens`.
6. Update the README: the Security model section (profiles are cooperative separation, not
   access control; anyone who can reach the APIs can read or write any profile, see D1), the Hub reference (new
   methods and arguments, `?profile=`, `SessionLimit`, `ContinueConversation`), the REST list
   (`/api/profiles`, header requirement), the Configuration list (`Sessions:*`) and the notes on
   the backups in *The index*.

---

## 7. Out of scope (for now)
- Deleting a profile, which has to clean up both databases. Add later with a cross-API cleanup
  endpoint.
- Sharing a note or session with the other person ("send to …"), or a combined household view.
- Per-profile font/theme preferences.
- Real authentication (see D1). If the app is ever exposed past the tailnet, this becomes
  necessary and the profile id would come from the authenticated user instead of a header.
- Letting a full session keep going (an "extend" button). If the limits turn out too tight, raise
  them in config instead. An escape hatch would bring the endless chat back.
- Summarising older turns *within* a session (rolling compaction). The hard limit plus recap
  gives the same benefit more predictably.
- Pagination of the sessions list (`ListAsync` caps at 100; fine for years of two people's use).
