# QwenStudio

**QwenStudio** is a high-performance, developer-centric agentic AI IDE and coding cockpit designed for local LLM inference (powered by `llama.cpp` and optimized for unified-memory architectures like AMD Strix Halo, Apple Silicon, and NVIDIA RTX).

Built on **.NET 9**, **Windows Forms**, and **Microsoft Edge WebView2**, QwenStudio bridges modern dark-mode web interfaces with bare-metal C# performance, delivering instantaneous token streaming, surgical multi-file codebase manipulation, and autonomous agent workflows.

---

## Key Highlights

- **Dual Engine Architecture:**
  - **Native Engine (Recommended):** High-efficiency, in-memory C# agent execution loop. Communicates directly with local/cloud LLMs over OpenAI-compatible SSE streaming endpoints with zero CLI subprocess overhead.
  - **Qwen CLI Runner:** Compatibility mode supporting `@qwen-code/qwen-code` npm distributions.
- **3-Tier Testing & Verification Dial:**
  - **Direct (No Tests):** Pure code synthesis without test suites for rapid scaffolding.
  - **Fast Sanity (Default):** Static UI contract verification, CSS specificity checks, and fast compilation tests (`dotnet build`, `node -c`). Keeps turn duration under 30 minutes.
  - **Rigorous (Autonomous):** Full automated test synthesis, test runner execution, failure diagnosis, and targeted iterative repair.
- **Context-Efficient AST Skeletons (`AstService`):**
  - Generates token-efficient symbol skeletons of C#, TypeScript, and JavaScript source files, collapsing implementation bodies.
  - Keeps repository architecture maps under 1,500 tokens for multi-file codebases, drastically reducing KV-cache prefill latency.
  - Features file-write-invalidated incremental caching.
- **Performance & Latency Optimizations:**
  - **Proactive Write Eviction:** Automatically compacts large file write payloads in conversation memory once committed to disk, preventing context bloat.
  - **Parallel Tool Dispatch:** Concurrently executes read-only tool calls (`read_file`, `grep_search`, `file_search`) via `Task.WhenAll`.
  - **Micro-Batched Token Streaming:** Throttles UI thread IPC updates across the WebView2 boundary with a 25ms flush window, keeping window interaction fluid at 50+ tokens/sec.
  - **Full Desktop Responsiveness:** Decoupled background thread dispatch with safe UI thread marshaling.

---

## Architecture Overview

```text
┌────────────────────────────────────────────────────────┐
│               QwenStudio Web UI (WebView2)            │
│       Monaco / Modern Dark Theme / Terminal Reg       │
└───────────────────────────▲────────────────────────────┘
                            │ PostWebMessageAsJson (Micro-batched)
┌───────────────────────────▼────────────────────────────┐
│                    MainForm (.NET 9)                   │
│        Thread-Safe Dispatch / Workspace Watcher       │
└─────────────┬───────────────────────────┬──────────────┘
              │                           │
┌─────────────▼───────────────┐ ┌─────────▼──────────────┐
│     NativeAgentEngine       │ │      AstService        │
│   In-Memory Agent Loop      │ │ Incremental Symbol Map │
│  Parallel Tool Execution    │ │  WORKSPACE_SKELETON    │
└─────────────┬───────────────┘ └────────────────────────┘
              │ SSE HTTP Streaming
┌─────────────▼──────────────────────────────────────────┐
│          Local llama-server / Cloud OpenAI APIs         │
│          (Qwen 3.8 27B / DeepSeek / Gemini / etc.)     │
└────────────────────────────────────────────────────────┘
```

---

## Getting Started

### Prerequisites
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10/11 with [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (pre-installed on modern Windows)
- A running OpenAI-compatible local server (e.g. `llama-server.exe` on port `8081`) or cloud API key.

### Building & Running

```bash
# Clone the repository
git clone https://github.com/btrahan1/QwenStudio.git
cd QwenStudio

# Build the solution
dotnet build QwenStudio.sln

# Run QwenStudio
dotnet run --project src/QwenStudio/QwenStudio.csproj
```

---

## License
MIT License. See [LICENSE](LICENSE) for details.

