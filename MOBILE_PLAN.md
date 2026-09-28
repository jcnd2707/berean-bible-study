# Mobile & Tablet Plan (Android)

Status: **implemented** on `feature/mobile-responsive`, verified on the target
phone (411×789) and tablet (533×752) — see [Implementation status](#implementation-status)
below for exactly what shipped, what was deliberately cut, and why.
Scope: `berean-web` (the Angular client). The APIs need no code changes. HTTPS comes from Tailscale in front of them (Phase 4).

## Implementation status

All six phases (§6) landed. Everything in the plan below was built as
described **except**:

- **The dictionary sheet's peek/half/full gesture** ships as a real drag
  (§3), but the "peek" state is just the smallest snap point, not a
  distinct collapsed preview.
- **"Look up word" is not a button** in the verse action bar (§3's phone
  mockup). The long-press lookup (§4.3) already covers word lookup, and a
  button with no word pre-chosen has no well-defined target.
- **Citation-chip and Strong's-toggle tooltips** (§4.3's tooltip-to-text
  list) were not converted to visible text — tapping a citation already
  opens the source (making the tooltip's extra detail redundant), and the
  Strong's toggle already shows its own on/off state. Only the disabled
  model/perspective selects (§4.3's own worked example) were converted.
- **Phone-landscape top-bar hide-on-scroll** (§3) is not implemented; the
  bottom-nav-becomes-a-side-rail part is. Hiding the top bar would need
  reaching into whichever child view's own scroll container is active.
- **The Android back button closes one thing at a time**, not a full
  nested history — see `back-stack.service.ts`'s doc comment. Opening the
  dictionary sheet while already on a non-Read tab means back closes the
  sheet, but a second press won't then return to Read.
- **Tailscale HTTPS itself (Phase 4) was intentionally left undone** — the
  app is ready for it (HTTPS-aware API URLs, PWA manifest), but the
  `tailscale serve` setup and path-stripping verification is the user's own
  machine configuration, done separately.
- A few structural simplifications: the tablet drag handle's snap points
  are continuous rather than fixed 35/50/70% stops, and `AiChatComponent`'s
  conversation state lives in the component instance rather than a shared
  service (only matters if a browser window is resized across the
  1200px desktop/tablet boundary mid-chat — not a concern for the actual
  target devices, which never cross that width just by rotating).

## Decisions (answered 2026-09-27)

| Question | Answer | What it changes |
|----------|--------|-----------------|
| Devices | ~5" phone, ~9" tablet | Target viewports below — now measured, not guessed. The tablet's portrait width (533px) is the key number, and it's narrower than the original ~600px guess. |
| Remote use | Yes, through Tailscale | HTTPS via `tailscale serve`. This makes the installable app (PWA) cheap, so it's in the plan (Phase 4). |
| Dictionary on phone | "Whatever is most intuitive" | **Bottom sheet over the reader.** You keep the verse in view while reading the definition, which is the pattern the major Bible apps use. On the tablet it's a tab in the side panel that opens itself when you look up a word. |
| PWA | "Whatever is easier" | Manifest-only PWA once Tailscale HTTPS is in place. No offline service worker: the app needs the PC anyway, and a service worker complicates updates. |
| Priority device | **Tablet** | Phases reordered: tablet shell before phone shell. |

### Target viewports (CSS pixels, what the browser actually lays out)

**Confirmed 2026-09-27** via `whatismyviewport.com` on the actual devices — these replace the earlier guesses.

| Device | Portrait | Landscape | Notes |
|--------|----------|-----------|-------|
| Phone | **411 × 789** | **789 × 411** | Taller than assumed (789px vs. the guessed 640px), so there's more usable vertical room than §1's "must be compact vertically" implied. Landscape height (411px) is still short enough to trigger the phone-landscape height rule in §3. |
| Tablet | **533 × 752** | **752 × 533** | Narrower than assumed (533px vs. the guessed 600px) — narrow enough that it falls *inside* the plan's original phone bucket (width < 560px). See the breakpoint fix below and in §2. |

**This breaks the plan's original width-only breakpoint.** A single "phone < 560px, tablet 560–1199px" width cut can't separate these two devices: the tablet's portrait width (533px) is below 560, so it would get the *phone* shell — the opposite of "tablet is the priority device." Worse, the phone's *landscape* width (789px) is well inside the 560–1199px tablet range, so a phone that gets rotated would jump to the tablet shell mid-session.

**The fix: classify by the viewport's shorter side, not by width alone.** The phone's shorter side is 411px in both orientations (rotating just swaps which axis is which); the tablet's shorter side is 533px in both orientations. That number is orientation-invariant, so it cleanly separates the two real devices without a landscape special case. Breakpoints are revised in §2.

The earlier draft's first pass put the phone/tablet cut at 700px, which had the same problem in the other direction (tablet portrait would have gotten the phone layout). Both draft cuts are now moot — see §2 for the corrected rule.

---

## 1. Why it looks bad on Android today

The client is built for one screen shape only: a wide desktop window used with a mouse. There are **zero `@media` queries** in the app. Specifically:

| # | Problem | Where | Effect on phone/tablet |
|---|---------|-------|------------------------|
| 1 | **Fixed four-pane layout sized in JS pixels.** Sidebar 138px + center (min 200) + right column (default 318, min 180) + dividers. Heights are `window height − 175` (dictionary) and `− 295` (chat). | [app.component.ts](berean-web/src/app/app.component.ts) `initSizes()` | Needs ≥ ~530px of width just for the minimums. A phone is 360–412px wide, so the right column (commentary + AI chat) is clipped off-screen by `overflow: hidden`. In landscape on a phone (~360px tall) the reader gets squeezed to its 100px minimum. |
| 2 | **Resizing only works with a mouse.** Dividers use `mousedown`/`mousemove`/`mouseup` and are 5px wide. | `app.component.ts` | You can't resize panes with a finger at all. |
| 3 | **Every `resize` event resets all pane sizes.** | `ngAfterViewInit` → `initSizes()` | On Android the soft keyboard opening fires `resize`, so tapping the chat input rearranges the whole screen. Rotating also wipes any sizes you set. (The listener is never removed, either.) |
| 4 | **`height: 100vh`.** | `app.component.ts`, `styles.scss` | On Android Chrome, `100vh` includes the area behind the address bar, so the bottom of the app (the chat input!) sits under the browser chrome. |
| 5 | **Tiny text and tap targets.** Base font 12px; 94 declarations at 9–11px; toolbar buttons are ~18px tall (`padding: 3px 9px; font-size: 10px`). Android's guideline is 48dp touch targets. | nearly every `.scss` | Hard to read, easy to mis-tap. |
| 6 | **Fake desktop chrome.** The title bar has menu words that do nothing and macOS "traffic light" dots. | [title-bar.component.ts](berean-web/src/app/features/shell/title-bar.component.ts) | Takes 38px of height on a small screen for no function. |
| 7 | **One crowded toolbar row.** ~13 controls in a 36px row that can't wrap. | [toolbar.component.ts](berean-web/src/app/features/shell/toolbar.component.ts) | Overflows or gets cut off on narrow screens. |
| 8 | **Word lookup is double-click only.** The dictionary says "Double-click any word". Single tap already toggles verse selection, so a double-tap selects then unselects the verse, and `dblclick` + `caretRangeFromPoint` is unreliable on touch. Long-press starts Android's own text selection instead. | [bible-reader.component.ts](berean-web/src/app/features/bible-reader/bible-reader.component.ts) `onVerseDoubleClick`, [dictionary-panel.component.html](berean-web/src/app/features/dictionary/dictionary-panel.component.html) | The dictionary, one of the core features, is basically unreachable on a phone. (The Strong's interlinear buttons are the only tap-friendly path.) |
| 9 | **Keyboard-only features.** Escape closes overlays / clears verse; Alt+←/→ changes chapter; Ctrl+F opens search. | [keyboard-shortcuts.service.ts](berean-web/src/app/core/services/keyboard-shortcuts.service.ts) | Without a keyboard there's no way to close some overlays except re-tapping the toolbar button. The Android **back button** leaves the app entirely, because the app has no routes or history entries. |
| 10 | **Information only in `title` tooltips.** e.g. why the model/perspective selects are disabled, Strong's toggle meaning, citation labels. | chat header, reader, toolbar | You can't hover on touch, so you never see these. |
| 11 | **AI chat header is a single row** holding status dot, label, state, RAG badge, mode toggle, two selects, quick-ask chips and two icon buttons. | [ai-chat.component.html](berean-web/src/app/features/ai-chat/ai-chat.component.html) | Unusable below ~500px wide. |
| 12 | **Compare shows up to 4 side-by-side columns.** | [compare-panel](berean-web/src/app/features/compare/) | 4 columns on a 400px screen is ~100px per column. |
| 13 | **Book reader has a fixed 200px chapter sidebar** and a 280px-min dropdown. | [book-reader.component.scss](berean-web/src/app/features/books/book-reader.component.scss) | Leaves ~160px for reading on a phone. |

### Good news from the analysis

The architecture is already well suited for this. **Each panel is a self-contained standalone component**, and they talk to each other only through shared signal services (`NavigationStateService`, `WordSelectionService`, `PreferencesService`). No panel knows where it sits on screen. So we can **rearrange the panels per device without rewriting them**. Most of the work is in the shell (`AppComponent`) plus CSS inside each panel.

One important catch: **state lives inside some components**. The AI chat keeps `messages`, the in-progress stream and the history view as component signals. Notes keeps unsaved text and saves it on a 1-second debounce. If a phone layout switched views with `@if`, it would destroy the chat mid-answer and could drop the last second of a note. Phone and tablet views must therefore **keep panels mounted and hide them** (`[hidden]` / CSS), not create and destroy them. (The existing right-panel tabs already use `@if`, so the notes edge case exists on desktop too. Worth fixing along the way.)

---

## 2. Approach: responsive web app, plus an optional PWA

| Option | Verdict |
|--------|---------|
| **A. Make the Angular app responsive** (three layouts, touch-aware) | **Recommended.** Same codebase, same deployment (IIS), and desktop keeps working exactly as today. |
| **B. PWA on top of A** (installable icon, full-screen, no browser bar) | **In the plan (Phase 4).** Needs HTTPS, which Tailscale provides almost for free. |
| C. Native wrapper (Capacitor / Trusted Web Activity) | **Not now.** The app is a thin client talking to APIs on your PC, so native adds an Android Studio toolchain, signing and store/sideload steps for little gain. TWA also needs a public HTTPS domain. Capacitor is the one fallback worth keeping in mind: if HTTPS turns out to be too painful, it can talk to the LAN over plain HTTP and still give a real app icon. |

### Two independent axes

1. **Layout, chosen by the viewport's shorter side plus its width.** Width alone can't separate the two real devices (§1), so the rule is: classify by `min(innerWidth, innerHeight)` for the phone/tablet cut, and by raw `innerWidth` for the tablet/desktop cut. This decides *which shell* renders:
   - **Phone**: shorter side < 480px. Covers 411×789 portrait and 789×411 landscape — the same 411px shorter side either way. One panel at a time, with bottom navigation. (Phone landscape, where height drops to 411px, additionally triggers the side-rail height rule in §3.)
   - **Tablet**: shorter side ≥ 480px **and** width < 1200px. Covers 533×752 portrait and 752×533 landscape. Reader plus a tabbed study panel: stacked in portrait, side by side in landscape.
   - **Desktop**: width ≥ 1200px, regardless of height. Today's four-pane layout, unchanged.
   - 480px is a round cutoff roughly midway between the measured phone (411px) and tablet (533px) shorter sides, with margin on both sides. It hasn't been checked against other phone models — if a wider Android phone (some large-screen phones report up to ~430–460px) turns up during testing, this number may need to move, but the mechanism (shorter-side, not raw width) doesn't change.
2. **Density, chosen by `(pointer: coarse)`.** This decides *how big things are*. A landscape tablet at 1280px gets the desktop layout but still gets finger-sized controls and readable text. A touchscreen laptop gets the same benefit.

Implementation: a small `LayoutService`. The phone/tablet/desktop cut above needs `window.innerWidth`/`innerHeight` compared in JS (there's no CSS media feature for "shorter side"), updated on `resize`; `(pointer: coarse)` can still use `window.matchMedia` directly. Exposes `layout = signal<'phone' | 'tablet' | 'desktop'>()` and `coarsePointer = signal<boolean>()`. No new dependencies are needed (Angular CDK's `BreakpointObserver` wouldn't cover the shorter-side logic anyway). **Panels** adapt to *the space they're given* with CSS **container queries** (`@container`), not viewport media queries. The same `<app-book-sidebar>` can then be a 138px column on desktop or a full-screen picker on a phone, with no JS branching.

---

## 3. Target layouts

### Phone (shorter side < 480px), portrait-first

```
┌──────────────────────────────┐
│ ◀  John 3  ▾   ▶     🔍   ⋮ │  ← top bar (56px). Tap the reference → book/chapter picker
├──────────────────────────────┤
│                              │
│   Active view (one of):      │
│   Read · Study · Ask         │
│                              │
│  ┌────────────────────────┐  │
│  │ Dictionary sheet       │  │  ← slides up when a word is looked up;
│  │ (peek / half / full)   │  │    swipe down to dismiss
│  └────────────────────────┘  │
├──────────────────────────────┤
│  📖 Read   📝 Study   💬 Ask  ⋯ More │  ← bottom nav + safe-area inset
└──────────────────────────────┘
```

- **Read**: the Bible reader, full width. Translation tabs scroll horizontally. Tapping a verse shows a **verse action bar** at the bottom: *Ask AI · Commentary · Notes · Cross-refs · Look up word*. This replaces today's "Selected · context sent to AI chat" strip and turns the desktop's side-by-side panels into one-tap jumps.
- **Study**: the existing right panel (Commentary / Notes / Cross-refs sub-tabs), full width.
- **Ask**: the AI chat, full height. The input is pinned above the keyboard.
- **More**: Search, Compare, My Notes, Books, font size A−/A+, reader theme.
- **Book/chapter picker**: `BookSidebarComponent` shown as a full-screen sheet, with a books grid then a chapter grid, 48px cells.
- **Overlays** (Search, Compare, Notes list, Books): full-screen views with a visible **✕ / back arrow**.
- **Title bar**: hidden.

**Phone landscape (789 × 411):** too short for two bars. When `max-height: 450px` (the real 411px height clears this), the bottom nav turns into a slim side rail on the left, and the top bar hides on scroll-down and comes back on scroll-up.

### Tablet (shorter side ≥ 480px, width < 1200px), the priority device

At 533px wide (narrower than the original ~600px guess), two side-by-side columns in portrait would leave the reader ~320px and the study panel ~210px, which is even more cramped than first thought. So the tablet layout **changes with orientation**:

```
Portrait (533 × 752)                   Landscape (752 × 533)
┌────────────────────────────┐         ┌──────────────────────────────────────────┐
│ ☰  ◀ John 3 ▾ ▶   Compare… │         │ ☰  ◀ John 3 ▾ ▶  Compare Search Notes …  │
├────────────────────────────┤         ├────────────────────────┬─────────────────┤
│                            │         │                        │ Comm│Notes│Xref │
│  Reader (full width)       │         │  Reader (~60%, ~450px) │ Dict│ Ask       │
│                            │         │                        │                 │
│                            │         │                        │ tabbed panel    │
├═════ drag handle ══════════┤         │                        │ (~40%, ~300px)  │
│ Comm│Notes│Xrefs│Dict│Ask  │         │                        │                 │
│ tabbed study panel         │         │                     ║ drag ║            │
└────────────────────────────┘         └────────────────────────┴─────────────────┘
```

- **Portrait:** reader on top, **tabbed study panel below** (*Commentary · Notes · Xrefs · Dictionary · Ask*), separated by a touch drag handle. Snap points are ~35% / 50% / 70%, and double-tapping the handle collapses the panel so you can read full-screen.
- **Landscape:** reader left, tabbed panel right, with a touch-enabled vertical divider. At 752px wide the panel's ~40% default share is only ~300px — tight for a 5-tab bar (Commentary/Notes/Xrefs/Dictionary/Ask). Worth checking early whether that needs a larger fixed minimum (e.g. ~320px, taken proportionally from the reader) or icon-only tabs under a container query at that width.
- **Book/chapter list:** a slide-in **drawer** (☰ or tap the reference), closed by default so the reader gets the full width.
- **Dictionary:** a tab in the study panel. Looking up a word switches to it automatically, and "Ask AI" / chat citations switch tabs the same way (§4.5).
- **Top bar:** the desktop toolbar at touch size. Secondary actions (font size, theme) go into ⋮ when space runs out. No fake title bar.
- Search, Compare, My Notes and Books still cover the reader area like today, but with a visible ✕ and back-button support.
- Panel sizes are remembered per orientation (in `localStorage`, like font size and theme today), so rotating doesn't lose your layout.

### Desktop (≥ 1200px)

Unchanged visually. Only these fixes apply: pointer events on dividers, `dvh`, no size reset on resize, and coarse-pointer density if it's a touchscreen. Large Android tablets in landscape (1280px+) land here, so the touch fixes matter.

---

## 4. Cross-cutting changes

### 4.1 Viewport & browser chrome
- `index.html` viewport: `width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-content`. The last option makes Chrome on Android shrink the layout when the keyboard opens, so the chat input stays visible.
- Add `<meta name="theme-color" content="#070e18">` so the Android status bar matches the app.
- Replace `100vh` with `100dvh` (keep `100vh` as a fallback line).
- Pad bottom bars with `env(safe-area-inset-bottom)` for gesture-navigation phones.
- Desktop shell: on `resize`, only **re-clamp** pane sizes to the new bounds instead of resetting them, and remove the listener in `ngOnDestroy`.

### 4.2 Design tokens for size & density
- Define tokens once in `styles.scss`: `--fs-2xs … --fs-lg` and `--tap-min`, `--pad-btn`, `--gap`.
- `@media (pointer: coarse)` raises them. The base goes from 12px to ~15px, the smallest text from 9px to ~12px, and `--tap-min` to 44–48px.
- Replace the 94 hard-coded small `font-size` values and the ~30 tiny paddings/heights with tokens. This is mechanical, but it's the biggest single edit. Doing it component by component keeps each PR reviewable.
- The reader already scales text from `PreferencesService.fontSize`, so that keeps working. On coarse pointers, raise the default from 13 to ~17. The stored preference still wins.

### 4.3 Touch interactions
- **Word lookup (replaces double-click):** listen to `selectionchange`. When the user long-presses a word (Android's native selection) inside the verse list, the verse action bar shows **"Look up ‘word’"**. This keeps Android's copy/share menu intact and doesn't fight it. Desktop keeps double-click. The Strong's interlinear buttons already work by tap and stay as they are. The dictionary hint text changes per device ("Long-press a word…" vs "Double-click…").
- **Dividers:** switch to Pointer Events (`pointerdown` + `setPointerCapture`, `pointermove`, `pointerup`) and add `touch-action: none`. Widen the hit area to ~20px on coarse pointers while keeping the visible 1px line.
- **Chapter swipe (optional):** a horizontal swipe on the reader goes to the previous/next chapter. It's ignored while text is selected or when the vertical movement dominates.
- **Tooltips:** anything a user *needs* moves out of `title` into visible text. For example, "Start a new conversation to change model" becomes helper text under the disabled select.

### 4.4 Android back button
Add a small `BackStackService`. Whenever an overlay, sheet or drawer opens (Search, Compare, Notes list, Books, dictionary sheet, picker, chat history, or a non-Read bottom tab), it calls `history.pushState`. On `popstate` it closes the top-most one. This gives the expected Android behaviour: **back closes the thing you opened** instead of leaving the app. The Escape handler in `KeyboardShortcutsService` can share the same "close top-most" logic.

### 4.5 Cross-panel requests must switch views on phone and tablet
Today, panels just react to signals because everything is always visible. On small screens the target may be hidden, so the shell must **also switch views** when these fire:
- `WordSelectionService.selection` changes → open the dictionary sheet (phone) or the Dictionary tab (tablet).
- `nav.requestedRightTab` (a chat citation was clicked) → switch to Study.
- `nav.openBookChapter` / `showBooks` → show the Books view.
- Verse action "Ask AI" → switch to Ask and focus the input.

---

## 5. Component-by-component changes

| Component | Phone | Tablet | Size |
|-----------|-------|--------|------|
| **AppComponent** (shell) | New phone shell: top bar, bottom nav, keep-mounted views, sheet host | Reader + tabbed study panel (stacked in portrait, side by side in landscape), book drawer | **L** (split into `desktop-shell`, `tablet-shell`, `phone-shell` components; `AppComponent` picks one via `@switch (layout.layout())`) |
| **TitleBar** | Hidden | Hidden | S |
| **Toolbar** | Becomes top bar: ◀ ref ▶, search icon, ⋮ menu. The context badge is dropped (the chat's context strip already shows it) | Wraps; secondary actions go to ⋮ | M |
| **BookSidebar** | Full-screen picker sheet: book grid then chapter grid (container query) | Drawer | M |
| **BibleReader** | Verse action bar, selection-based lookup, horizontally scrolling translation tabs, larger verse numbers and interlinear buttons | Same | M |
| **DictionaryPanel** | Bottom sheet (peek → half → full) | Tab in side panel | M |
| **RightPanel** (Commentary/Notes/Xrefs) | Study view; sub-tabs become a full-width segmented control | Becomes the tablet's study panel: tabs gain Dictionary + Ask, and tabs are kept mounted rather than recreated with `@if` | M |
| **AiChat** | Header collapses to: status dot + title + ⚙ + history + new. **⚙ opens a settings sheet** (Mode, Perspective, Model, with helper text). Quick-ask chips move to a horizontally scrolling row above the input. Bubbles use ~95% width. Citations and "Save to notes" get 44px targets | Same collapse when narrow (container query) | M–L |
| **Compare** | **Interleaved view**: for each verse, show each chosen translation stacked, labelled. Much more readable than columns on a phone. The "+ Add" dropdown becomes a sheet. Sync-scroll is hidden (not needed when interleaved) | Portrait: interleaved. Landscape: 2 columns, interleaved if 3–4 | M |
| **SearchPanel** | Filters collapse behind a "Filters" button; results are full width | Same, filters inline | S–M |
| **NotesList / Notes** | Full width; textarea grows; the delete button gets a proper target | — | S |
| **BookReader** | Chapter sidebar becomes a "Chapters" button that opens a drawer; the book dropdown becomes a sheet; reading pane is full width | Sidebar collapsible | M |
| **CrossReferences / Commentary** | Mostly tokens and spacing | — | S |

---

## 6. Phased rollout

Each phase can ship on its own, and desktop stays working throughout.

The order is **tablet first**, as requested. Each phase is a separate PR and desktop stays working throughout.

**Phase 0: Foundations (small, immediate visible win)**
- Viewport meta, `theme-color`, `dvh`, and the desktop resize fix (§4.1).
- `LayoutService` (layout + orientation + coarse pointer).
- Design tokens and coarse-pointer density (§4.2). Start with the shell, toolbar, reader, chat, dictionary and right panel, since those are on screen in the tablet layout.
- Hide the title bar on non-desktop layouts.
- *Result:* readable text and finger-sized buttons, and the chat input is no longer hidden. The layout itself is still the desktop one.

**Phase 1: Touch interactions (shared by tablet and phone)**
- Pointer-event dividers/drag handles, selection-based word lookup and the verse action bar, visible close buttons on all overlays, the Android back stack, and view switching on cross-panel requests (§4.3–§4.5).
- Keep panels mounted instead of recreating them with `@if`, and flush the pending note save when a notes view hides, so no view switch can lose chat or notes.

**Phase 2: Tablet shell (priority device)**
- Split `AppComponent` into `desktop-shell` / `tablet-shell` (the phone shell comes in Phase 3).
- Tablet shell: portrait stacked (reader over tabbed study panel, with snap-point handle), landscape side by side, book drawer, Dictionary + Ask as study-panel tabs, per-orientation sizes remembered.
- Adapt the components that appear in it: AiChat header collapse + ⚙ settings sheet (container query, so it also fits a ~380px landscape panel), Compare interleaved/2-column, BookReader chapter drawer, Search filters.
- *Result:* the tablet is fully usable in both orientations.

**Phase 3: Phone shell**
- Top bar, bottom nav (side rail in landscape), keep-mounted Read/Study/Ask views, book/chapter picker sheet, **dictionary bottom sheet** (peek / half / full), More menu.
- Mostly reuses what Phase 2 built. The new pieces are the bottom nav, the sheets and the landscape height rule.

**Phase 4: Tailscale HTTPS + installable app**

Current state: the client builds API addresses as `http://<page host>:5121` and `:5050`. This already works over Tailscale as plain HTTP, but Chrome won't install an HTTP site as an app, and it won't allow an HTTPS page to call HTTP APIs (mixed content).

Plan: put **one HTTPS address** in front of all three services with `tailscale serve`, and route by path:

| Path on `https://<pc-name>.<tailnet>.ts.net` | Forwards to |
|---|---|
| `/` | IIS web app (`berean-web`) |
| `/resource/…` | Resource API (`localhost:5121`) |
| `/agent/…` (incl. SignalR `/agent/hubs/chat`) | Agent service (`localhost:5050`) |

- **One origin** means no CORS changes, and Tailscale provides a real trusted certificate, so there's nothing to install on the devices.
- **Only `src/environments/*.ts` changes.** When the page is served over `https:`, use `${origin}/resource` and `${origin}/agent`; otherwise keep today's `http://host:5121/5050`, so desktop and LAN access are unaffected.
- **To verify during this phase:** that `tailscale serve` strips the path prefix before forwarding (otherwise the APIs would need a path base), and that the SignalR WebSocket upgrade passes through. If either fails, the fallback is separate HTTPS ports per service (`--https=8443` etc.) plus adding that origin to both APIs' `Cors:AllowedOrigins`. That's still config-only.
- **Side benefit:** the APIs only need to listen on `localhost`, since Tailscale is the way in. That's safer than exposing ports on the home network. Keep LAN access if you still want it at home without Tailscale.
- **PWA:** `manifest.webmanifest` (name "Berean", icons, `display: standalone`, `theme_color`/`background_color` #070e18) plus a `<link rel="manifest">`. No offline service worker. If Chrome's install prompt insists on one, add a minimal pass-through worker, not a caching one. Then **Add to Home screen → Install** on each device: full-screen, no browser bar, and ~56px more height on the 5" phone.

**Phase 5: Polish**
- Chapter swipe, tooltip-to-text cleanup, the rest of the token migration, and phone-landscape tuning.

---

## 7. Testing

- **Iterate** in Chrome DevTools device mode at your devices' confirmed sizes: **533×752 / 752×533** (tablet) and **411×789 / 789×411** (phone). Also check the current desktop size for regressions.
- **Verify on real hardware** via USB and `chrome://inspect` remote debugging. Device mode can't reproduce the soft keyboard, the back button, long-press selection or `dvh` behaviour.
- **Flow checklist per layout:** pick book/chapter · switch translation · select a verse → Ask AI · look up a word (long-press + Strong's) · open a commentary from a chat citation · write a note and switch views without losing it · Compare 3 translations · search with filters · open a book chapter · change model/perspective · browse and reopen chat history · rotate mid-chat-stream (the conversation must survive) · Android back closes each overlay in order.
- **Unit tests:** `LayoutService` breakpoint mapping and `BackStackService` open/close ordering are pure logic, and fit the existing esbuild + node test style in `berean-web/tests/`.

---

## 8. Still open

1. ~~Exact viewport sizes~~ **Resolved 2026-09-27**: phone 411×789, tablet 533×752 (§1). This also forced a real fix, not just a number swap — the phone/tablet cut now keys off the shorter viewport side, not raw width (§2).
2. **Tailscale setup:** deliberately deferred — the user is doing `tailscale serve`/MagicDNS setup separately, outside this plan. The app-side half of Phase 4 (HTTPS-aware API URLs, PWA manifest) is done; only this half remains.
3. ~~Tablet landscape study-panel width~~ **Addressed**: `.study-pane` has a 280px `min-width` in landscape (`tablet-shell.component.ts`), taken from the reader rather than letting the panel shrink to ~300px. The icon-only-tabs fallback wasn't built — untested whether 280px is comfortable enough with all 5 tabs, or just adequate.
