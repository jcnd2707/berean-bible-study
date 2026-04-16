# Berean Web

A full-featured SDA Bible study web application built with Angular 19. Connects to the [Berean Resource API](../BereanResourceApi) for Bible text, commentaries, dictionaries, and notes, and to the [Hybrid Agent API](../HybridAgent.API) for AI-powered research via SignalR.

---

## Features

### Bible Reader
- Multi-translation tab strip — switch between all available modules instantly
- Book and chapter navigation via the left sidebar (OT/NT grouped)
- Verse selection — click a verse to set it as context for the AI agent
- Double-click any word to look it up in the dictionary panel
- Strong's number resolution for translations with embedded Strong's (e.g. AKJV+)

### Commentary Panel
- All available commentary modules in a tab strip (Barnes, Matthew Henry, SDABC, etc.)
- Auto-scrolls to the entry covering the selected verse
- Switches commentary when the active module changes

### Dictionary Panel
- **Strong's / BDB** — looked up by Strong's number when a compatible translation is active
- **Easton's** — plain word lookup for any translation
- Structured display: original script, transliteration, phonetic, definitions, Strong's entry
- KJV Occurrences section omitted for readability

### Cross-References
- Shown in the right panel alongside Commentary and Notes
- Activate by selecting a verse — lists all cross-references sorted by vote count
- Click any reference to navigate directly to that passage

### Notes
- Per-verse and per-chapter notes stored via the Resource API
- Auto-saves 1 second after you stop typing
- Notes are scoped to the current reference (e.g. `Gen.1.1` or `Gen.1`)
- Delete button removes the note for the current reference

### Search
- Full-text search across the active Bible module
- Filter by Testament (All / OT / NT) and optionally scope to a single book
- Results grouped by book, search term highlighted in amber
- Click any result to navigate to that verse and close search

### Compare
- Side-by-side view of up to 4 translations for the same chapter
- Synchronized scrolling across all columns (toggle off for independent scroll)
- Add/remove translations via the pill UI
- Clicking a verse in any column sets it as the active verse globally

### AI Research Agent
- Connects to the Hybrid Agent API via SignalR at `http://localhost:5050/hubs/chat`
- Streams responses token by token with a blinking cursor
- Context strip automatically includes: translation, book, chapter, selected verse text, active commentary entry, and last dictionary lookup
- Quick-ask chips: Explain, Original languages, EGW
- Reset button clears the conversation on both client and server

### Navigation
- **Typed reference** — click the reference box in the toolbar, type `John 3:16` or `Gen 1`, press Enter
- **Keyboard shortcuts:**
  - `Ctrl+F` / `Cmd+F` — open Search
  - `Escape` — close Search / Compare, or deselect verse
  - `Alt+←` / `Alt+→` — previous / next chapter

### Layout
- Three-column shell: book sidebar (fixed) · center (Bible reader + dictionary) · right (commentary/notes/xrefs + AI chat)
- All panel dividers are drag-to-resize — horizontal and vertical
- Search and Compare replace the verse list as full overlays, toggled from the toolbar

---

## Prerequisites

- Node.js 20+
- Angular CLI 19
- [Berean Resource API](../BereanResourceApi) running at `https://localhost:7105`
- [Hybrid Agent API](../HybridAgent.API) running at `http://localhost:5050` (optional — AI chat only)

## Setup

```bash
npm install
npm start
```

App runs at `http://localhost:4200`.

### CORS (Resource API)

Add to your ASP.NET Core `Program.cs`:

```csharp
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p =>
        p.WithOrigins("http://localhost:4200")
         .AllowAnyHeader()
         .AllowAnyMethod()));
// ...
app.UseCors();
```

### Dev cert

```bash
dotnet dev-certs https --trust
```

---

## Project Structure

```
src/app/
├── core/
│   ├── models/
│   │   └── index.ts                  # All API response interfaces
│   └── services/
│       ├── bible.service.ts           # /api/bible/* calls
│       ├── resources.service.ts       # /api/resources/* + /api/bible/{id} details
│       ├── commentary.service.ts      # /api/commentary/*
│       ├── cross-references.service.ts
│       ├── dictionary.service.ts      # /api/dictionary/*
│       ├── notes.service.ts           # /api/notes/*
│       ├── agent-hub.service.ts       # SignalR connection to Agent API
│       ├── navigation-state.service.ts # Shared signals: location, verse, books, overlays
│       ├── word-selection.service.ts  # Shared signal: double-clicked word
│       └── keyboard-shortcuts.service.ts
├── features/
│   ├── shell/
│   │   ├── title-bar.component.ts
│   │   └── toolbar.component.ts      # Editable reference box, Compare, Search buttons
│   ├── book-sidebar/                 # Book list + chapter grid
│   ├── bible-reader/                 # Translation tabs, verse list, search/compare overlays
│   ├── right-panel/                  # Commentary / Notes / Cross-refs tab host
│   ├── commentary/
│   ├── notes/
│   ├── cross-references/
│   ├── dictionary/
│   │   └── definition-parser.ts      # Parses Strong's definition strings into sections
│   ├── search/
│   ├── compare/
│   └── ai-chat/
└── app.component.ts                  # Three-column shell with drag-to-resize dividers
```

---

## API Endpoints Used

| Endpoint | Purpose |
|---|---|
| `GET /api/resources/bibles` | List available Bible modules |
| `GET /api/bible/{moduleId}` | Module details (title, hasStrongs) |
| `GET /api/bible/{moduleId}/books` | Book list for sidebar |
| `GET /api/bible/{moduleId}/{book}/{chapter}` | Chapter verses |
| `GET /api/bible/{moduleId}/{book}/{chapter}/{verse}` | Single verse with Strong's words |
| `GET /api/bible/{moduleId}/search` | Full-text search |
| `GET /api/resources/commentaries` | List commentary modules |
| `GET /api/commentary/{moduleId}/{book}/{chapter}` | Commentary entries |
| `GET /api/resources/dictionaries` | List dictionary modules |
| `GET /api/dictionary/{moduleId}/details` | Module metadata (isStrongs) |
| `GET /api/dictionary/{moduleId}/lookup` | Word or Strong's number lookup |
| `GET /api/crossreferences/{book}/{chapter}/{verse}` | Cross-references for a verse |
| `GET /api/notes/{reference}` | Load note |
| `POST /api/notes/{reference}` | Save/update note |
| `DELETE /api/notes/{reference}` | Delete note |
