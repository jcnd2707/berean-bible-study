# Implementation report

All six phases of the original implementation plan (removed once this report confirmed they were done) are implemented and verified, with the exceptions listed under "Needs you". **Nothing is committed** (you didn't ask for commits); review `git status` first. One tag exists: `archive/multi-agent`, made before the Car and C# agents were removed.

## Results

Measured with the eval harness on 20 questions, retrieval against a copy of your real index.

| | Baseline (old code) | Now |
|---|---|---|
| Questions that got no sources at all | 9 of 20 (the "what is…" path fetched nothing) | **0 of 20** |
| Share of retrieved sources that were Adventist (contested questions) | **100%** | **0%** with the toggle off |
| Questions with any Adventist source, toggle off | every one that retrieved anything | **0 of 16** |
| Most sources one module supplied | 3 | **2** (the cap) |
| Toggle on | same retrieval as off | separate `ADVENTIST SOURCES` block, `[A#]` ids |

Why the baseline looked like that: your index held **only** EGW books (28,068 chunks). Commentaries had never been indexed: `AllowedCommentaryModules: null` binds to an empty list, and the old indexer read an empty list as "allow none".

Answers, same 20 questions through the Claude Code provider:

| Model | Tokens / question | Time / question | Notes |
|---|---|---|---|
| Opus | 5.7k | 44 s | Text first, views by tradition with sources, states when its sources come from one tradition |
| Sonnet | 4.7k | 51 s | Similar structure and citation habits |
| llama3.1:8b (Ollama) | – | 40–140 s | No citations, ignores the method; one question hit Ollama's 100 s timeout |

The eval checks every citation against what was actually in the prompt. One of the four SDA answers from each model broke the rule "neutral analysis cites only `[S#]`, the Adventist section only `[A#]`". I tightened the SDA instruction and re-ran those four on Opus: **0 of 4 problems**.

Runs are in `eval/runs/` (only `*-baseline` is git-tracked): `baseline`, `neutral-retrieval`, `neutral-retrieval-llama`, `claudecode-opus`, `claudecode-sonnet`, `claudecode-opus-sda-recheck`, `claudecode-opus-compare`.

## Verified in a real browser

I ran the web app in Edge against scratch copies of both APIs: chat connects, a sourced answer streams with citation chips, the sources list is grouped by tradition, clicking a chip opens that commentary at the verse, Save to notes appends to the note, the Strong's toggle shows numbers and opens the dictionary, the history list works, and a reload resumes the conversation. That found three bugs (items 2 and 5 below) that unit tests had missed, and I added light markdown rendering because the model's answers are markdown.

## What was done, by phase

- **0.** Build output untracked and ignored; machine paths moved to `appsettings.Development.json`; Car and C# agents removed; `OpenAI:ApiKey` gone.
- **1.** xUnit project (119 tests), 20 questions with rubrics, `eval/Berean.Eval` with tradition breakdown, cap check, leak check, citation check, token counts.
- **2.** Tradition profiles served by the Resource API; `ModuleId`/`Tradition` on every chunk; in-place backfill (no re-embedding, backs the index up first); `RetrievalFilter` with per-module cap; two-pass router; labelled `[S#]`/`[A#]` context; method-based prompts in `Berean.Core/Agent/Prompts/*.md`; Compare mode.
- **3.** Provider factory (Claude Code, Anthropic, OpenAI, Ollama); manual tool loop and the assistant "reminder" replaced by function invocation; verdict step removed; usage logging, context cap and history trimming; the Claude Code client (locked down, session resume, transcript fallback), tested against a fake `claude`.
- **4.** Renamed to `Berean.*`; layout and one file per tool as in 4.3; legacy file mode removed; `SelectAgent` → `StartConversation` (`StartSession` kept as an alias); READMEs rewritten.
- **5.** Real streaming, tool-activity line, clickable citations and sources list, cross-reference tool using local data, Strong's occurrences (API + `find_word_occurrences` tool + interlinear toggle), save to notes (`POST /api/notes/{ref}/append`), saved conversations (`chat.db`, resume, list, delete).

## Beyond the plan (things I found while doing it)

1. **Commentary chunk ids collided.** Chunk ids restarted at 0 for every entry, so `INSERT OR IGNORE` dropped all but the first entry of each commentary in the database.
2. **The router misread 26 of the API's 66 book abbreviations** (`Jhn`, `Luk`, `1Co`… were unknown, and `1Jn`/`2Jn`/`3Jn` resolved to John). The web client writes those abbreviations into its context tags, so verse-pinned retrieval silently failed for about 40% of the books. Fixed with a test over all 66.
3. **Verse ranges lost their end verse** (`Romans 8:28-30`), and "Explain John 1:1" wasn't recognised as a verse.
4. **A tool wrapper never forwarded its JSON schema**, which a hosted model needs to know a tool's parameters.
5. Saved sources were stored PascalCase, so restored answers lost their chips; the Strong's toggle keyed off a module flag that is false for `akjvstrong`.
6. The KJV module returns each verse many times; verse text is now de-duplicated.

## Where I departed from the plan, and why

- **Commentaries are not auto-indexed** (`AutoIndexMissingModules: false`). Embedding runs at about 3 chunks a second on this CPU, and a full commentary is hundreds of thousands of chunks. Instead, **verse questions read every commentary straight from the API** (one entry per module, so each tradition weighs the same), and chapter-only questions rank entries by the question's key words. Semantic search over commentaries needs indexing; turn the setting on to build them (nothing already indexed is touched).
- Verse text also comes from the API, not the index (as in 2.5), and word lookups gained a transliteration index so "nephesh" finds H5315.
- The shared `BibleKnowledge` (index loaded once, not per connection) and removal of legacy file mode came earlier than Phase 4 because Phase 3 rewrote those classes.
- The Anthropic and OpenAI providers are unit-tested but **have not run against the live APIs**.

## Needs you

1. **Finish the IIS setup (needs an elevated PowerShell).** Both sites are already deployed: `D:\Bible Study\ResourceAPI` (port 5121) and `D:\Bible Study\Agent` (port 5050, was already IIS-hosted, so no Windows service was needed). Both are running again and serving the new builds. The web app is built and sitting in `D:\Bible Study\Web` (with its `web.config`), but the IIS site for it (port 4200) doesn't exist yet. Run `powershell -ExecutionPolicy Bypass -File .\deploy\finish-iis-setup.ps1`: it creates that site, runs the BibleAgent pool as your account (the Claude Code login lives in your profile; the default pool identity can't see it), stops the pools idling out, starts everything and checks it. Until you do, the live Agent replies "Could not find claude.exe" on the Claude Code models (checked against the running site) (local Ollama models still work).
2. Roll back if needed: `D:\Bible Study\deploy-backups\` holds the previous builds (rebuilt from the `archive/multi-agent` tag, because the automatic backup step failed) and the old configs.
3. The first chat migrates your real index after copying it to `bible.rag.db.pre-tradition.bak` (135 MB). The migration was only ever run on copies.
4. Decide about indexing the commentaries (hours; see above), and make sure `claude` is logged in (or set `ANTHROPIC_API_KEY` and change `Llm:Provider`). Claude Code here is for personal use on your own machine; check Anthropic's current terms.
5. Commit in whatever grouping you like. The plan suggested one commit per step and phase tags.

## Verifying it yourself

```bash
dotnet build Berean.sln && dotnet test Berean.Core.Tests
cd berean-web && npm run test:unit
dotnet run --project eval/Berean.Eval -- --label check --retrieval-only     # no model needed
```
