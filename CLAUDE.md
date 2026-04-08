# ClickyWindows — Agent Instructions

<!-- This is the single source of truth for all AI coding agents. -->
<!-- AGENTS.md spec: https://github.com/agentsmd/agents.md — supported by Claude Code, Cursor, Copilot, Gemini CLI, and others. -->

## Overview

Windows WPF tray application that ports the macOS [Clicky](https://github.com/farzaa/clicky) AI companion to Windows. Lives entirely in the system tray (no taskbar icon, no main window). A transparent full-screen overlay hosts a blue triangle cursor that can fly to and point at UI elements on any connected monitor. Push-to-talk (Ctrl+Alt hold) streams microphone audio and a screenshot directly to the **Gemini Live API** over a bidirectional WebSocket. Gemini handles speech recognition, LLM reasoning, and TTS audio synthesis in one session — no separate transcription or TTS service.

The Gemini API key is stored in Windows Credential Manager. Nothing sensitive ships in the app or config files.

## Architecture

- **App Type**: System tray-only (`ShowInTaskbar=false`, `WindowStyle=None`), no main window
- **Framework**: WPF (.NET 8) with Win32 P/Invoke for overlay, global hooks, DPI, and Credential Manager
- **Pattern**: Event-driven with a `PushToTalkController` state machine as the central coordinator
- **AI / STT / TTS**: Gemini Live API (`gemini-3.1-flash-live-preview`) — single bidirectional WebSocket handles all three
- **Screen Capture**: `BitBlt` via GDI+, multi-monitor aware
- **Voice Input**: Push-to-talk via `NAudio` `WasapiCapture` (16kHz mono PCM)
- **Audio Playback**: `NAudio` `WasapiOut` (24kHz mono PCM from Gemini)
- **Element Pointing**: Gemini embeds `[POINT:x,y:label:screenN]` tags in responses. `PointParser` maps coordinates to the correct monitor and `FlightPathAnimator` animates the triangle via a cubic Bezier arc.
- **API Key Storage**: Windows Credential Manager via `CredRead`/`CredWrite` P/Invoke. Set by the first-run `SetupWizardWindow`.

### Data Flow

```
Hotkey press ─┬─ MicrophoneRecorder ──────────► GeminiLiveService.SendAudioAsync (PCM 16kHz)
              │                                           │
              └─ ScreenCaptureService ──► SendScreenshotAsync (JPEG, first monitor only)
                                                          │
                                      Gemini Live WebSocket (bidi)
                                                          │
          ┌───────────────────────────────────────────────┼──────────────────────────┐
          ▼                                               ▼                          ▼
inputTranscription (user text)        modelTurn.inlineData (PCM 24kHz)   outputTranscription (text)
          │                                               │                          │
          │                                    AudioPlaybackService                  │
          │                                      (WasapiOut)                         ▼
          └─────────────────────────────► ConversationHistory ◄── PointParser → FlightPathAnimator
```

On hotkey release, `CompleteTurnAsync` sends `realtimeInput.audioStreamEnd`. Gemini's VAD finalizes the turn and the server sends `turnComplete`, which drains TTS playback and saves the turn to history.

### State Machine (`PushToTalkController`)

```
IDLE → [Ctrl+Alt press] → RECORDING → [release] → PROCESSING → [first audio] → SPEAKING → [turnComplete + drain] → IDLE
```

Echo prevention: hotkey press during SPEAKING or PROCESSING cancels the active session, disposes the Gemini WebSocket, and transitions immediately to RECORDING.

### Key Architectural Decisions

- **Overlay**: `AllowsTransparency=True` + `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW` in `SourceInitialized`. Hardware-accelerated on WDDM.
- **Render loop**: `DispatcherTimer` at 16ms — not `CompositionTarget.Rendering` (throttles to ~50fps, dotnet/wpf#1908).
- **Keyboard hook**: `WH_KEYBOARD_LL` callback does the minimum (set flag, return). 300ms OS timeout — callback silently removed after ~10 violations. Health check re-registers every 5s.
- **Monitor enumeration**: `EnumDisplayMonitors` + `GetDpiForMonitor` P/Invoke — not `Screen.AllScreens` (confirmed DPI bugs, dotnet/winforms#10952).
- **Audio playback**: `WasapiOut` shared mode (~10–30ms latency) with 250ms pre-buffer before `Play()`.
- **Gemini WebSocket**: URI is `wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key=…`. First message is a `setup` frame with `responseModalities: ["AUDIO"]`, `inputAudioTranscription: {}`, `outputAudioTranscription: {}`, and `speechConfig`. Blocks on `setupComplete` (5s timeout) before streaming.
- **History via systemInstruction**: `initialHistoryInClientContent` causes `InvalidPayloadData` socket close on `gemini-3.1-flash-live-preview`. Prior turns are embedded into `systemInstruction` at setup time instead. See `GeminiLiveService.BuildSystemInstruction`.
- **Session lifecycle**: Each push-to-talk turn creates a new `GeminiLiveService`. The previous session's disposal task is stored in `_geminiDisposalTask` so the new turn awaits full WebSocket teardown before opening a fresh connection (prevents socket leaks). All disposal runs **outside** `_stateLock` to avoid deadlocks with playback callbacks.
- **Turn versioning**: `_turnVersion` is incremented on every state transition. Late-arriving callbacks from a superseded session are dropped by comparing the captured version.
- **Namespace collisions**: The project uses both `UseWPF` and `UseWindowsForms`. Qualify ambiguous types: `System.Windows.Application`, `System.Windows.Media.Color`, `System.Windows.Point`. All P/Invoke declarations live in `Interop/NativeMethods.cs`.

### `[POINT]` Coordinate Format

Gemini emits `[POINT:x,y:short_label:screenN]` where `x,y` are physical pixels relative to the monitor's top-left and `N` is the 0-based monitor index. The format is specified in the system instruction built by `GeminiLiveService.BuildSystemInstruction`. `PointParser` validates and clamps coordinates; `FlightPathAnimator` flies the triangle via a cubic Bezier arc with a 40px tolerance zone. Point tags are stripped from text before it is saved to `ConversationHistory`.

## Key Files

| File | Lines | Purpose |
|------|-------|---------|
| `App.xaml.cs` | ~134 | Entry point. Bootstraps tray icon, overlay, hotkey service, and `PushToTalkController`. Runs first-run `SetupWizardWindow` if the Gemini key is missing. |
| `AppSettings.cs` | ~28 | Strongly-typed settings bound from `appsettings.json` (hotkey, audio, Gemini model/voice). |
| `Input/PushToTalkController.cs` | ~519 | Central state machine. Owns the Gemini session lifecycle, microphone streaming, screenshot capture, audio playback, point parsing, conversation history, and all turn transitions. |
| `AI/GeminiLiveService.cs` | ~379 | Gemini Live API WebSocket client. Manages setup handshake, bidirectional audio/text streaming, `setupComplete` gate, and graceful teardown. Fires `AudioReceived`, `TextCompleted`, `InputTranscriptionReceived`, `TurnComplete`, and `ErrorOccurred` events. |
| `AI/ConversationHistory.cs` | ~76 | Loads/saves conversation turns as JSON. Text-only — screenshots are never stored. |
| `AI/CredentialStore.cs` | ~76 | Reads and writes API keys via Windows Credential Manager (`CredRead`/`CredWrite`). Target: `ClickyWindows/GeminiApiKey`. |
| `Input/GlobalHotkeyService.cs` | ~204 | `WH_KEYBOARD_LL` low-level keyboard hook. Publishes press/release events for the configured hotkey. Health-check timer re-registers the hook every 5s. |
| `Overlay/OverlayManager.cs` | ~225 | Manages the full-screen transparent `OverlayWindow`. Exposes state transitions, speech bubble text, waveform levels, flight animations, and monitor geometry to the controller. |
| `Overlay/OverlayWindow.xaml.cs` | ~120 | WPF window with `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW`. Hosts the animated blue triangle and response text. |
| `Overlay/FlightPathAnimator.cs` | ~77 | Animates the triangle to a target coordinate via a cubic Bezier arc. 40px arrival tolerance. |
| `Audio/MicrophoneRecorder.cs` | ~78 | `WasapiCapture` wrapper. Streams 16kHz mono PCM buffers and reports audio levels for waveform display. |
| `Audio/AudioPlaybackService.cs` | ~149 | `WasapiOut` wrapper. Accepts streaming PCM chunks, pre-buffers 250ms before playback starts, and fires `PlaybackCompleted` when the stream drains. |
| `Audio/AudioLevelMonitor.cs` | ~54 | Computes RMS levels from PCM buffers for waveform visualization. |
| `Screen/ScreenCaptureService.cs` | ~92 | Multi-monitor screenshot capture via GDI+ `BitBlt`. Returns JPEG base64 per monitor. |
| `Screen/MonitorEnumerator.cs` | ~82 | `EnumDisplayMonitors` + `GetDpiForMonitor` P/Invoke. Returns DPI-aware `MonitorInfo` for all connected displays. |
| `AI/PointParser.cs` | ~58 | Parses `[POINT:x,y:label:screenN]` tags from Gemini text. Validates and clamps to monitor bounds. |
| `AI/ProxyClient.cs` | ~58 | Unused legacy HTTP client (retained for reference, not wired up). |
| `Tray/TrayIconManager.cs` | ~104 | `NotifyIcon` lifecycle. Tray menu with status, settings link, and quit. |
| `Setup/SetupWizardWindow.xaml.cs` | ~71 | First-run wizard. Prompts for the Gemini API key and saves it to Credential Manager. |
| `Interop/NativeMethods.cs` | ~156 | All Win32 P/Invoke declarations (overlay window flags, hooks, DPI, Credential Manager). |
| `Helpers/CoordinateTransform.cs` | ~42 | Converts physical pixel coordinates to WPF device-independent units for overlay positioning. |
| `Helpers/DpiHelper.cs` | ~18 | Per-monitor DPI scale helpers. |

## Build & Run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Build
dotnet build src/ClickyWindows/ClickyWindows.csproj

# Run
dotnet run --project src/ClickyWindows

# Release publish (self-contained, win-x64)
dotnet publish src/ClickyWindows/ClickyWindows.csproj \
  --configuration Release --runtime win-x64 \
  --self-contained true --output dist/
```

There are no automated tests yet.

## Configuration

`src/ClickyWindows/appsettings.json` — controls hotkey, audio, and the Gemini model/voice:

```json
{
  "Hotkey": { "Key": "Menu", "Modifiers": "Control" },
  "Audio": { "SampleRate": 16000, "PreBufferMs": 250, "PlaybackBufferSeconds": 45 },
  "Gemini": {
    "Model": "models/gemini-3.1-flash-live-preview",
    "VoiceName": "Aoede",
    "ConnectTimeoutMs": 5000
  }
}
```

The Gemini API key is **not** stored here — it lives in Windows Credential Manager and is entered once via the first-run setup wizard.

## Code Style & Conventions

### Naming

- Be as clear and specific as possible with variable and method names.
- Optimize for clarity over concision. A developer with zero context should immediately understand what a variable or method does from its name alone.
- Use longer names when they improve clarity. No single-character names.
- When passing arguments, keep the same names as the source variables — do not shorten or abbreviate.

### Code Clarity

- Clear is better than clever. Do not reduce lines of code if doing so makes the code harder to understand.
- Write more lines when additional lines improve readability.
- When a name alone cannot fully explain something, add a comment explaining what is happening and why.

### C#/WPF Conventions

- All UI updates must run on the WPF Dispatcher (`Application.Current?.Dispatcher.BeginInvoke(...)`).
- Use `async`/`await` for all asynchronous operations.
- All Win32 P/Invoke declarations belong in `Interop/NativeMethods.cs`.
- Qualify ambiguous types: `System.Windows.Application`, `System.Windows.Media.Color`, `System.Windows.Point`.
- Comments should explain "why", not just "what" — especially for non-obvious Win32 bridging.

### Do NOT

- Do not add features, refactor code, or make improvements beyond what was asked.
- Do not add docstrings, comments, or type annotations to code you did not change.
- Do not use `git add -A` or `git add .` — stage specific files only.

## Git Workflow

- Branch naming: `feature/description` or `fix/description`
- Commit messages: imperative mood, concise, explain the "why" not the "what"
- Do not force-push to main/master

## Self-Update Instructions

<!-- AI agents: follow these instructions to keep this file accurate. -->

When you make changes that affect the information in this file, update it to reflect those changes:

1. **New files**: Add an entry to the Key Files table with purpose and approximate line count.
2. **Deleted files**: Remove the entry.
3. **Architecture changes**: Update the Architecture section if you introduce new patterns, services, or structural changes.
4. **Build changes**: Update build commands if the build process changes.
5. **New conventions**: If the user establishes a new coding convention, add it to the conventions section.
6. **Line count drift**: If a file's line count changes by more than 50 lines, update the table.

Do NOT update this file for minor bug fixes or changes that don't affect documented architecture or conventions.
