# Berean Web

Angular 19 frontend for the Berean Bible study app.

## Prerequisites

- Node.js 20+
- npm 10+
- Berean Resource API running at `https://localhost:7105`

## Setup

```bash
# 1. Install dependencies
npm install

# 2. Start the dev server
npm start
```

The app will be available at `http://localhost:4200`.

> **CORS**: Make sure your Resource API allows `http://localhost:4200`.
> In your ASP.NET Core `Program.cs` add:
> ```csharp
> builder.Services.AddCors(options =>
>     options.AddDefaultPolicy(p =>
>         p.WithOrigins("http://localhost:4200")
>          .AllowAnyHeader()
>          .AllowAnyMethod()));
> // ...
> app.UseCors();
> ```

> **SSL cert**: Since the API is on `https://localhost:7105`, your browser
> must trust the dev cert. Run `dotnet dev-certs https --trust` once if
> you haven't already.

## Project structure

```
src/app/
├── core/
│   ├── models/            # Typed interfaces (all API shapes)
│   │   └── index.ts
│   └── services/
│       ├── bible.service.ts          # /api/bible/* calls
│       ├── resources.service.ts      # /api/resources/* calls
│       └── navigation-state.service.ts  # Active location signals
├── features/
│   └── bible-reader/      # First panel — book/chapter navigation + verse display
└── app.component.ts       # Shell layout (panels added here as they're built)
```

## What's built

### Bible reader panel
- Loads available translations from `/api/resources/bibles`
- Loads book list (OT/NT grouped) from `/api/bible/{moduleId}/books`
- Fetches chapter from `/api/bible/{moduleId}/{book}/{chapter}`
- Click a verse to select it — updates the global `NavigationStateService`
- Prev/Next chapter navigation

### NavigationStateService (signals)
Holds the active `BibleLocation` (moduleId, book, chapter, verse).
All future panels (commentary, dictionary, AI chat) inject this service
to know what to display. The `contextStrip` computed property is the
payload that will be sent to the Agent API.

## Next panels to add
1. **Commentary** — inject `NavigationStateService`, react to `location` signal,
   call `CommentaryService` (not yet created).
2. **Dictionary** — word lookup triggered by verse click or Strong's number.
3. **Notes + AI chat** — reads `contextStrip` signal, sends to Agent via SignalR.
