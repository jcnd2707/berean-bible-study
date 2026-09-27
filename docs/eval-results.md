# Eval results and bugs found along the way

Measured with the eval harness (`eval/Berean.Eval`) on the 20 questions in `eval/questions.json`, retrieval run against a copy of the real index.

## Retrieval, before and after the tradition-aware rewrite

| | Before | After |
|---|---|---|
| Questions that got no sources at all | 9 of 20 | **0 of 20** |
| Share of retrieved sources that were Adventist (contested questions) | **100%** | **0%** with the toggle off |
| Questions with any Adventist source, toggle off | every one that retrieved anything | **0 of 16** |
| Most sources one module supplied | 3 | **2** (the per-module cap) |
| Toggle on | same retrieval as off | separate `ADVENTIST SOURCES` block, `[A#]` ids |

The "before" numbers look that bad because the index held only Ellen G. White books (28,068 chunks) at the time — commentaries had never been indexed, since `AllowedCommentaryModules: null` bound to an empty list, and the indexer read an empty list as "allow none".

## Answer quality, same 20 questions, Claude Code provider

| Model | Tokens / question | Time / question | Notes |
|---|---|---|---|
| Opus | 5.7k | 44 s | Text first, views by tradition with sources, states when its sources come from one tradition |
| Sonnet | 4.7k | 51 s | Similar structure and citation habits |
| llama3.1:8b (Ollama) | – | 40–140 s | No citations, ignores the method; one question hit Ollama's 100 s timeout |

The eval checks every citation against what was actually in the prompt. One of the four SDA answers from each model broke the rule that neutral analysis cites only `[S#]` and the Adventist section only `[A#]`. Tightening the SDA prompt instruction and re-running those four on Opus brought that to 0 of 4.

## Bugs the eval and manual browser testing caught

1. **Commentary chunk ids collided.** Ids restarted at 0 for every entry, so `INSERT OR IGNORE` silently dropped all but the first entry of each commentary.
2. **The router misread 26 of the API's 66 book abbreviations** (`Jhn`, `Luk`, `1Co`… were unknown, and `1Jn`/`2Jn`/`3Jn` all resolved to John). The web client writes these abbreviations into its context tags, so verse-pinned retrieval silently failed for about 40% of the books — caught by testing against all 66 books instead of a handful.
3. **Verse ranges lost their end verse** (`Romans 8:28-30`), and "Explain John 1:1" wasn't recognized as a verse reference at all.
4. **A tool wrapper never forwarded its JSON schema**, which a hosted model needs to call a tool's parameters correctly (it worked fine against Ollama's manual tool loop, which doesn't need the schema).
5. Saved sources were stored PascalCase by `System.Text.Json`'s default, so restored conversations lost their citation chips; the Strong's toggle also keyed off a module flag that was false for `akjvstrong`.
6. The KJV module returns each verse multiple times from the API; verse text needed de-duplicating before display.

## Verifying it yourself

```bash
dotnet build Berean.sln && dotnet test Berean.Core.Tests
cd berean-web && npm run test:unit
dotnet run --project eval/Berean.Eval -- --label check --retrieval-only     # no model needed
```
