# HybridAgent — Multi-Domain with RAG

Three specialized AI agents, each backed by local Ollama + RAG + cloud verdict.

## Agents

| Agent | Local model | Specialization |
|---|---|---|
| Car Diagnostics | `llama3.2:3b` | OBD codes, symptoms, repair guidance |
| Bible Research | `llama3:8b` | Hermeneutics, cross-references, languages |
| C# Troubleshooting | `deepseek-coder:6.7b` | Exceptions, patterns, .NET runtime |

## Quick start

```bash
# 1. Pull the models
ollama pull llama3.2:3b
ollama pull llama3:8b
ollama pull deepseek-coder:6.7b

# 2. Pull the embedding model (used by RAG — one model for all agents)
ollama pull nomic-embed-text

# 3. Set your OpenAI key
$env:OPENAI_API_KEY = "sk-..."          # PowerShell
export OPENAI_API_KEY=sk-...            # bash

# 4. Run
dotnet run
```

## Adding domain knowledge (RAG)

Drop plain `.txt` files into the relevant docs folder, then restart the agent.
The first run indexes and embeds all files — subsequent runs load from the saved index.

```
docs/
  car/
    obd_codes.txt          ← full OBD-II code reference
    toyota_camry_tsbs.txt  ← technical service bulletins
    repair_manual_v6.txt   ← engine repair procedures
  bible/
    commentary_romans.txt  ← Romans commentary
    strongs_hebrew.txt     ← Strong's Hebrew concordance
    historical_context.txt ← 1st century historical background
  csharp/
    dotnet8_whats_new.txt  ← .NET 8 release notes
    aspnetcore_patterns.txt
    ef_core_troubleshooting.txt
```

**Format tips:**
- Plain text works best — strip HTML/PDF formatting before saving
- One topic per file makes retrieval more precise
- Shorter files (< 50 KB) chunk more cleanly than large ones

## Project structure

```
HybridAgent/
├── RAG/
│   ├── DocumentChunker.cs   # splits text into overlapping chunks
│   ├── EmbeddingService.cs  # calls Ollama /api/embed
│   ├── VectorStore.cs       # cosine similarity search + JSON persistence
│   └── RagPipeline.cs       # orchestrates chunk → embed → search → inject
├── Agents/
│   ├── AgentFactory.cs      # per-domain config, tools, system prompts
│   ├── DiagnosticAgent.cs   # Phase 1: Ollama tool loop + RAG context
│   └── VerdictAgent.cs      # Phase 2: cloud model final verdict
├── Tools/
│   └── ToolRegistry.cs      # AIFunction registration + invocation tracking
├── Models/
│   └── Models.cs            # DiagnosisSummary, ToolResult, AgentConfig
├── HybridPipeline.cs        # wires RAG + DiagnosticAgent + VerdictAgent
└── Program.cs               # agent selector + chat loop
```

## How RAG works

```
Your question
    │
    ▼
EmbeddingService.EmbedAsync()     ← Ollama nomic-embed-text (local)
    │  query vector
    ▼
VectorStore.Search()              ← cosine similarity against indexed chunks
    │  top-5 matching chunks
    ▼
Injected into system prompt       ← DiagnosticAgent sees your docs
    │
    ▼
Ollama reasons over your content  ← answers grounded in your documents
    │
    ▼
Cloud model final verdict
```

## Swapping the cloud provider

Only `HybridPipeline.cs` imports the OpenAI SDK.
To use Azure OpenAI, replace the `cloudClient` line:

```csharp
IChatClient cloudClient = new AzureOpenAIClient(
    new Uri("https://YOUR-RESOURCE.openai.azure.com/"),
    new Azure.AzureKeyCredential(config.AzureApiKey))
    .AsChatClient(config.CloudModel);
```