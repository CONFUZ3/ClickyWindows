# ClickyWindows

A Windows port of [Clicky](https://github.com/farzaa/clicky) — an AI voice companion that lives as a blue triangle overlay on your screen. Hold a hotkey, speak, and it responds with voice while flying its triangle cursor to locations it references on your display.

Powered entirely by the **Gemini Live API** — one WebSocket handles speech recognition, AI reasoning, and text-to-speech. No separate transcription or TTS service needed.

---

## Demo

> *Hold Ctrl+Alt, ask "what's in the top-right corner?", release — the triangle flies there as Gemini responds.*

---

## Features

- **Push-to-talk** — hold Ctrl+Alt to record, release to send
- **Full-screen transparent overlay** — the blue triangle stays on top across all monitors
- **Animated pointer** — Gemini references screen locations with `[POINT:x,y:label:screen0]` tags; the triangle flies there via a Bezier arc
- **Unified AI pipeline** — speech-to-text, reasoning, and voice output in a single Gemini Live WebSocket session
- **Conversation history** — multi-turn context is carried across interactions
- **Interrupt** — press the hotkey mid-response to cancel TTS and start a new recording immediately
- **System tray app** — no taskbar icon, no main window; lives quietly in the background

---

## Prerequisites

- Windows 10/11 (x64)
- An API key for your chosen voice provider:
  - **Gemini** (default) — get one at [aistudio.google.com](https://aistudio.google.com/apikey). Note: since **June 19, 2026** Google rejects *unrestricted* keys — restrict your key to the Gemini API in AI Studio, or create a new key.
  - **OpenAI** — get one at [platform.openai.com](https://platform.openai.com/api-keys); the key needs Realtime API access.
- No Node.js, no proxy server, no environment variables

---

## Installation

### Option A — Download a release

1. Download `ClickyWindows-vX.X.X-win-x64.zip` from the [Releases](../../releases) page
2. Extract the zip anywhere
3. Run `ClickyWindows.exe`

### Option B — Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Clone
git clone https://github.com/your-username/clickywindows.git
cd clickywindows

# Run directly
dotnet run --project src/ClickyWindows

# Or build a self-contained release (no SDK needed to run)
dotnet publish src/ClickyWindows/ClickyWindows.csproj \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  --output dist/
```

---

## First-run setup

On first launch a setup wizard appears. Paste the API key for your selected provider (Gemini by default — see `Provider` in `appsettings.json`) and click **Save and Start**. You can fill in both the Gemini and OpenAI keys if you plan to switch providers.

Your key is stored in **Windows Credential Manager** — never written to any file on disk. To update it later, right-click the tray icon → **Manage API Keys...**

---

## Usage

| Action | How |
|--------|-----|
| Ask a question | Hold **Ctrl+Alt**, speak, release |
| Interrupt a response | Hold **Ctrl+Alt** while Gemini is still talking |
| Quit | Right-click tray icon → **Quit** |

The triangle animates to whatever screen element Gemini references in its reply.

---

## Configuration

`appsettings.json` (sits next to the `.exe`) lets you tweak defaults without recompiling:

```json
{
  "Provider": "Gemini",
  "Hotkey": {
    "Key": "Menu",
    "Modifiers": "Control"
  },
  "Audio": {
    "SampleRate": 16000,
    "PreBufferMs": 250,
    "PlaybackBufferSeconds": 45
  },
  "Gemini": {
    "Model": "models/gemini-3.1-flash-live-preview",
    "VoiceName": "Aoede",
    "ConnectTimeoutMs": 5000
  },
  "OpenAi": {
    "Model": "gpt-realtime-2",
    "VoiceName": "marin",
    "PointingModel": "gpt-4o",
    "ConnectTimeoutMs": 5000
  }
}
```

`Key: "Menu"` is the right Alt key. `Modifiers: "Control"` means the left Ctrl must be held simultaneously. Gemini voice names (as of Gemini 2.5): `Aoede`, `Charon`, `Fenrir`, `Kore`, `Puck`.

### Choosing a provider

Set `"Provider"` to `"Gemini"` (default) or `"OpenAI"`. Both run the same experience — voice answer plus the triangle flying to the referenced UI element — using only that provider's API key:

- **Gemini** uses the Gemini Live API for voice and a Gemini vision model for pointing.
- **OpenAI** uses the OpenAI Realtime API for voice (24 kHz audio) and a vision chat model (`gpt-4o` by default) for pointing.

These are the two providers that support a realtime voice **and** vision (screenshot) session in one pass. After switching `Provider`, make sure that provider's key is set (tray → **Manage API Keys...**).

---

## Architecture

```
Hotkey press ─┬─ MicrophoneRecorder (16kHz PCM) ──────────► GeminiLiveService.SendAudioAsync
              │                                                        │
              └─ ScreenCaptureService (JPEG) ──► SendScreenshotAsync  │
                                                                       │
                                             Gemini Live WebSocket (bidi)
                                                                       │
              ┌────────────────────────────────────────────────────────┤
              ▼                                 ▼                      ▼
  inputTranscription (user words)   modelTurn.inlineData (PCM 24kHz)  outputTranscription
              │                                 │
              │                       AudioPlaybackService (WasapiOut)
              │                                 │
              └──────────────► ConversationHistory ◄── PointParser → FlightPathAnimator
```

**State machine:** `IDLE → RECORDING → PROCESSING → SPEAKING → IDLE`

Each push-to-talk turn opens a fresh `GeminiLiveService` WebSocket session. The previous session is gracefully torn down before the new one connects, preventing socket leaks. Prior conversation turns are injected into the Gemini `systemInstruction` at setup time (the `initialHistoryInClientContent` API path causes a socket close on `gemini-3.1-flash-live-preview`).

**API key security:** The Gemini key is read from Windows Credential Manager at runtime and passed in memory only. It never appears in config files, logs, or source code.

---

## Logs

Logs are written to `%APPDATA%\ClickyWindows\logs\` and rotate daily (7-day retention). They contain no API keys or audio data.

---

## Credits

- Original macOS [Clicky](https://github.com/farzaa/clicky) by [@farzaa](https://github.com/farzaa)
- Audio via [NAudio](https://github.com/naudio/NAudio)
- AI / STT / TTS via [Google Gemini Live API](https://ai.google.dev/gemini-api/docs/live)
