# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Git Workflow

This repo has a single initial commit. When making changes, amend that commit and force-push rather than creating new commits, unless the user asks for a new commit.

```bash
# Stage specific files only — never use git add -A or git add .
git add src/ClickyWindows/Foo.cs src/ClickyWindows/Bar.cs

# Before committing, verify no secrets are staged
git diff --cached --name-only | grep -iE "\.env|\.vars|secret|key"

# Amend (keeps history clean for a young repo)
git commit --amend --no-edit

# Force push (master branch)
git push --force origin master

# Release: delete old tag and re-push to retrigger Actions
git push origin :refs/tags/v1.0.0
git tag v1.0.0
git push origin v1.0.0
```

**Never commit:**
- `src/ClickyWindows.Proxy/.dev.vars` (real API keys — gitignored)
- `bin/`, `obj/`, `dist/`, `.wrangler/`, `node_modules/`
- `plan.md`

## Build Commands

The .NET 8 SDK is installed at `E:/dotnet/` (not in system PATH). Always use the full path:

```bash
# Build
E:/dotnet/dotnet.exe build src/ClickyWindows/ClickyWindows.csproj

# Run
E:/dotnet/dotnet.exe run --project src/ClickyWindows

# Release publish (self-contained, win-x64)
E:/dotnet/dotnet.exe publish src/ClickyWindows/ClickyWindows.csproj --configuration Release --runtime win-x64 --self-contained true --output dist/
```

There are no automated tests yet.

## Architecture

ClickyWindows is a WPF tray application that ports the macOS [Clicky](https://github.com/farzaa/clicky) AI companion to Windows. It shows a blue triangle cursor overlay, records voice via push-to-talk (Ctrl+Alt hold), sends screenshots + transcript to Claude via a Cloudflare Worker proxy, speaks the response via ElevenLabs TTS, and animates the triangle to `[POINT]` coordinates Claude references.

### Data flow

```
Hotkey press → MicrophoneRecorder → (PCM stream) → TranscriptionService (AssemblyAI v3 WS)
                                                           ↓ transcript (end_of_turn)
ScreenCaptureService → ClaudeService (SSE via proxy) ← ConversationHistory
                              ↓ streamed text chunks
                        PointParser → FlightPathAnimator (Bezier arc on overlay)
                              ↓ full text
                        TtsService → AudioPlaybackService (WasapiOut)
```

### Key architectural decisions (from plan.md)

- **Overlay**: `AllowsTransparency=True` + `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW` set in `SourceInitialized`. Hardware-accelerated on WDDM (the "forces software rendering" claim is outdated).
- **Render loop**: `DispatcherTimer` at 16ms — NOT `CompositionTarget.Rendering` which throttles to ~50fps (dotnet/wpf#1908).
- **Keyboard hook**: `WH_KEYBOARD_LL` callback is minimal (set flag only, return immediately). 300ms OS timeout — callback silently removed after ~10 violations. Health check re-registers every 5s.
- **Monitor enumeration**: `EnumDisplayMonitors` + `GetDpiForMonitor` P/Invoke — NOT `Screen.AllScreens` (confirmed DPI bugs in dotnet/winforms#10952).
- **Audio playback**: `WasapiOut` (shared mode, ~10-30ms latency) with 250ms pre-buffer before `Play()` — NOT `WaveOutEvent` (~300ms legacy latency).
- **AssemblyAI**: v3 API (`wss://streaming.assemblyai.com/v3/ws`), `speech_model` is **required** (no default), use `"u3-rt-pro"`. Use `receiveTurn` + `end_of_turn: true` as transcript finalization trigger. Send `Terminate` on close.
- **Conversation history**: Text-only (no screenshots in history). Only current screenshots sent per request. Prevents 2–9MB base64 context bloat.
- **API keys**: Never in the app — all live in the Cloudflare Worker (`src/ClickyWindows.Proxy/`). The proxy forwards `/chat` to Anthropic and `/tts` to ElevenLabs. AssemblyAI WebSocket connects directly using `ASSEMBLYAI_API_KEY` env var.

### `[POINT]` coordinate format

Claude emits `[POINT:x,y:label:screenN]` where `x,y` are physical pixels relative to the screenshot's monitor top-left, and `N` is the 0-based monitor index. `PointParser` validates and clamps to monitor bounds. `FlightPathAnimator` flies the triangle via cubic Bezier arc with 40px tolerance zone at destination.

### State machine (`PushToTalkController`)

```
IDLE → [Ctrl+Alt press] → RECORDING → [release] → PROCESSING → [Claude done] → SPEAKING → [TTS done] → IDLE
```
Echo prevention: hotkey during SPEAKING stops TTS first, then transitions to RECORDING.

## Configuration

`src/ClickyWindows/appsettings.json` — set `ProxyUrl` to deployed Cloudflare Worker URL before running.

### Proxy deployment
```bash
cd src/ClickyWindows.Proxy
npm install
wrangler secret put ANTHROPIC_API_KEY
wrangler secret put ELEVENLABS_API_KEY
wrangler secret put ASSEMBLYAI_API_KEY
wrangler deploy
```

## Namespace / ambiguity notes

Because the project uses both `UseWPF` and `UseWindowsForms`, several types are ambiguous and must be qualified:
- Use `System.Windows.Application` not `Application`
- Use `System.Windows.Media.Color` not `Color`
- Use `System.Windows.Point` not `Point`
- All P/Invoke declarations live in `Interop/NativeMethods.cs`
