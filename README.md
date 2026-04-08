# ClickyWindows

A Windows port of [Clicky](https://github.com/farzaa/clicky) — an AI voice companion that lives as a blue triangle overlay on your screen. Hold a hotkey, speak, and it responds with voice while animating its triangle cursor to locations it references on your screen.

**Powered by:** Claude (Anthropic) · ElevenLabs TTS · AssemblyAI real-time transcription

---

## Features

- **Push-to-talk voice input** — hold Ctrl+Alt to record, release to process
- **Full-screen overlay** — transparent triangle cursor visible across all monitors
- **Animated pointer** — Claude can reference screen locations with `[POINT:x,y]` and the triangle flies there via Bezier arc
- **Real-time transcription** — AssemblyAI v3 WebSocket streams audio as you speak
- **Voice responses** — ElevenLabs TTS speaks Claude's reply
- **Conversation history** — multi-turn context across interactions
- **System tray app** — runs quietly in the background

---

## Prerequisites

You need API keys for:

| Service | Purpose | Free tier? |
|---------|---------|------------|
| [Anthropic](https://console.anthropic.com/) | Claude AI responses | Pay-per-use |
| [ElevenLabs](https://elevenlabs.io/) | Text-to-speech voice | Yes (limited) |
| [AssemblyAI](https://www.assemblyai.com/) | Real-time transcription | Pay-per-use |

The proxy (`src/ClickyWindows.Proxy/`) is a small Node.js server that holds your Anthropic and ElevenLabs keys — it runs locally on your machine so keys never live inside the app itself.

---

## Setup

### Step 1: Configure the proxy

```bash
cd src/ClickyWindows.Proxy
npm install

# Copy the example and fill in your keys
copy dev.vars.example .dev.vars
```

Edit `.dev.vars`:

```
ANTHROPIC_API_KEY=sk-ant-...
ELEVENLABS_API_KEY=sk_...
ASSEMBLYAI_API_KEY=...
```

### Step 2: Start the proxy

```bash
cd src/ClickyWindows.Proxy
npx wrangler dev
```

This starts the proxy at `http://localhost:8787`. Leave this terminal running.

> The app's default `ProxyUrl` is already `http://localhost:8787` — no config change needed.

### Step 3: Set your AssemblyAI key

AssemblyAI connects directly from the app via a WebSocket. Set it as a Windows environment variable:

```powershell
[Environment]::SetEnvironmentVariable("ASSEMBLYAI_API_KEY", "your_key_here", "User")
```

Then restart your terminal / the app so it picks up the new variable.

### Step 4: Run the app

**Option A — Download pre-built release** (no .NET SDK needed):

Go to the [Releases](../../releases) page and download the latest `ClickyWindows.zip`. Extract and run `ClickyWindows.exe`.

**Option B — Build from source**:

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Quick run (development)
dotnet run --project src/ClickyWindows

# Self-contained release build (produces ClickyWindows.exe in dist/)
.\publish\build.ps1
```

---

## Usage

1. Start the proxy (`npx wrangler dev` in `src/ClickyWindows.Proxy/`)
2. Launch `ClickyWindows.exe` — a tray icon appears
3. Hold **Ctrl+Alt** and speak your request
4. Release to stop recording — the app transcribes, sends to Claude, and speaks the response
5. The blue triangle animates to any screen location Claude references

**During a response:** Hold Ctrl+Alt again to interrupt TTS and start a new recording.

**Exit:** Right-click the tray icon → Exit.

---

## Configuration

`src/ClickyWindows/appsettings.json`:

| Key | Default | Description |
|-----|---------|-------------|
| `ProxyUrl` | `http://localhost:8787` | Proxy server URL |
| `Hotkey.Key` | `Menu` | Push-to-talk key (Menu = Alt) |
| `Hotkey.Modifiers` | `Control` | Modifier key |
| `AssemblyAI.SpeechModel` | `u3-rt-pro` | AssemblyAI model |
| `Claude.Model` | `claude-sonnet-4-6` | Anthropic model ID |
| `Claude.MaxHistory` | `10` | Conversation turns to retain |
| `ElevenLabs.VoiceId` | `21m00Tcm4TlvDq8ikWAM` | ElevenLabs voice (Rachel) |

---

## Deploying the proxy to Cloudflare (optional)

If you want the proxy running 24/7 without keeping a terminal open, you can deploy it to a free Cloudflare Worker:

```bash
cd src/ClickyWindows.Proxy
npx wrangler deploy

# Set secrets in the cloud instead of .dev.vars
npx wrangler secret put ANTHROPIC_API_KEY
npx wrangler secret put ELEVENLABS_API_KEY
npx wrangler secret put ASSEMBLYAI_API_KEY
```

Then update `ProxyUrl` in `appsettings.json` to your worker URL:
`https://clicky-windows-proxy.<your-subdomain>.workers.dev`

---

## Architecture

```
Hotkey press → MicrophoneRecorder → PCM stream → AssemblyAI v3 WebSocket
                                                        ↓ transcript
ScreenCaptureService → ClaudeService (SSE via proxy) ← ConversationHistory
                              ↓ streamed text
                        PointParser → FlightPathAnimator (Bezier arc overlay)
                              ↓ full text
                        TtsService → AudioPlaybackService (WasapiOut)
```

- **Overlay**: WPF transparent window with `WS_EX_TRANSPARENT | WS_EX_LAYERED` — click-through, always on top
- **Audio**: WasapiOut shared mode (~10-30ms latency)
- **Proxy**: Local Wrangler dev server (or Cloudflare Worker) at `/chat` (Anthropic SSE) and `/tts` (ElevenLabs)

---

## Building / Contributing

```bash
# Build
dotnet build src/ClickyWindows/ClickyWindows.csproj

# Release publish (self-contained win-x64)
dotnet publish src/ClickyWindows/ClickyWindows.csproj \
  --configuration Release --runtime win-x64 --self-contained true --output dist/
```

.NET 8 SDK required. No test suite yet.

---

## Credits

- Original macOS [Clicky](https://github.com/farzaa/clicky) by [@farzaa](https://github.com/farzaa)
- This Windows port uses WPF, AssemblyAI v3, Claude, and ElevenLabs
