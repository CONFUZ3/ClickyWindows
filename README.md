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

You need API keys for three services:

| Service | Purpose | Free tier? |
|---------|---------|------------|
| [Anthropic](https://console.anthropic.com/settings/keys) | Claude AI responses | Pay-per-use |
| [ElevenLabs](https://elevenlabs.io/app/settings/api-keys) | Text-to-speech voice | Yes (limited) |
| [AssemblyAI](https://www.assemblyai.com/dashboard) | Real-time transcription | Pay-per-use |

No Node.js, no proxy server, no environment variables needed.

---

## Setup

1. Download `ClickyWindows.zip` from the [Releases](../../releases) page, extract, and run `ClickyWindows.exe`
2. A setup wizard appears on first launch — enter your three API keys
3. Click **Save and Start**

Keys are stored in **Windows Credential Manager** (not in any file). To update keys later: right-click the tray icon → **Manage API Keys...**

---

## Usage

1. Launch `ClickyWindows.exe` — a tray icon appears
2. Hold **Ctrl+Alt** and speak your request
3. Release to stop recording — the app transcribes, sends to Claude, and speaks the response
4. The blue triangle animates to any screen location Claude references

**Interrupt:** Hold Ctrl+Alt during a response to stop TTS and start a new recording.

**Exit:** Right-click the tray icon → Quit.

---

## Configuration

`src/ClickyWindows/appsettings.json` (optional overrides):

| Key | Default | Description |
|-----|---------|-------------|
| `Hotkey.Key` | `Menu` | Push-to-talk key (Menu = Alt) |
| `Hotkey.Modifiers` | `Control` | Modifier key |
| `AssemblyAI.SpeechModel` | `u3-rt-pro` | AssemblyAI model |
| `Claude.Model` | `claude-sonnet-4-6` | Anthropic model ID |
| `Claude.MaxHistory` | `10` | Conversation turns to retain |
| `ElevenLabs.VoiceId` | `21m00Tcm4TlvDq8ikWAM` | ElevenLabs voice (Rachel) |

---

## Architecture

```
Hotkey press → MicrophoneRecorder → PCM stream → AssemblyAI v3 WebSocket (direct)
                                                        ↓ transcript
ScreenCaptureService → ClaudeService (SSE, direct) ← ConversationHistory
                              ↓ streamed text
                        PointParser → FlightPathAnimator (Bezier arc overlay)
                              ↓ full text
                        TtsService → AudioPlaybackService (WasapiOut)
```

- **Overlay**: WPF transparent window with `WS_EX_TRANSPARENT | WS_EX_LAYERED` — click-through, always on top
- **Audio**: WasapiOut shared mode (~10-30ms latency)
- **API keys**: Stored in Windows Credential Manager; never written to disk in plaintext

---

## Building from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Build
dotnet build src/ClickyWindows/ClickyWindows.csproj

# Release publish (self-contained win-x64, no SDK needed to run)
dotnet publish src/ClickyWindows/ClickyWindows.csproj \
  --configuration Release --runtime win-x64 --self-contained true --output dist/
```

---

## Credits

- Original macOS [Clicky](https://github.com/farzaa/clicky) by [@farzaa](https://github.com/farzaa)
- This Windows port uses WPF, AssemblyAI v3, Claude, and ElevenLabs
