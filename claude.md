# WisperFlow - AI Coding Assistant Guide

## Project Overview

WisperFlow is a **Windows push-to-talk dictation application** built with WPF/.NET 8. It captures voice via configurable hotkeys, transcribes using AI services, optionally polishes/formats the text using LLMs, and injects the result into any focused application.

### Key Features
- **Push-to-talk recording** with configurable hotkeys (hold to record, release to process)
- **Multiple transcription backends**: OpenAI Whisper, Deepgram (streaming/batch), Groq Whisper, local Whisper.net
- **Text polishing** via OpenAI, Groq, Cerebras, or local LLM (LLamaSharp)
- **Command mode**: Voice queries to AI chatbots (ChatGPT, Gemini) via embedded WebView2 browser
- **Notes mode**: Structured formatting with bullet points and headings
- **Universal text injection**: Works with any Windows application via clipboard
- **System tray integration**: Runs in background with tray icon
- **Real-time streaming**: Deepgram streaming for live transcription feedback

---

## Tech Stack

| Component | Technology |
|-----------|------------|
| Framework | .NET 8 / WPF |
| Audio | NAudio, NAudio.Lame |
| Transcription | OpenAI Whisper API, Deepgram, Whisper.net (local) |
| LLM Polish | OpenAI GPT, Groq, Cerebras, LLamaSharp (local) |
| Browser | WebView2 (Microsoft Edge) |
| Tray Icon | Hardcodet.NotifyIcon.Wpf |
| Spell Check | WeCantSpell.Hunspell |
| Settings | JSON + Windows Credential Manager |

---

## Project Structure

```
WisperFlow/
├── WisperFlow.sln              # Solution file
├── WisperFlow/                 # Main application
│   ├── App.xaml(.cs)           # Application entry point, service initialization
│   ├── DictationBar.xaml(.cs)  # Main floating dictation UI bar (bottom of screen)
│   ├── SettingsWindow.xaml(.cs)# Settings configuration with sidebar navigation
│   ├── OverlayWindow.xaml(.cs) # Recording/transcribing status overlay
│   ├── FloatingBrowserWindow.xaml(.cs)  # Embedded AI chat browser (ChatGPT/Gemini)
│   ├── FloatingContainerWindow.xaml(.cs) # Container for floating windows
│   ├── FloatingTranscriptWindow.xaml(.cs) # Transcript display window
│   ├── ScreenshotOverlayWindow.xaml(.cs) # Screenshot capture overlay
│   ├── ModelManagerWindow.xaml(.cs)      # Local model management
│   │
│   ├── Models/
│   │   ├── AppSettings.cs      # Settings model (hotkeys, languages, models, etc.)
│   │   └── ModelInfo.cs        # AI model definitions and metadata
│   │
│   ├── Services/
│   │   ├── DictationOrchestrator.cs    # **CORE**: Main workflow coordinator
│   │   ├── HotkeyManager.cs            # Global hotkey detection (polling-based)
│   │   ├── AudioRecorder.cs            # Microphone recording (NAudio)
│   │   ├── TextInjector.cs             # Clipboard paste with Win32 APIs
│   │   ├── TextPolisher.cs             # Legacy polisher (deprecated)
│   │   ├── CredentialManager.cs        # Secure API key storage
│   │   ├── SettingsManager.cs          # JSON settings persistence
│   │   ├── TrayIconManager.cs          # System tray integration
│   │   ├── ScreenshotService.cs        # Screen capture utilities
│   │   ├── ServiceFactory.cs           # Dependency injection factory
│   │   ├── ModelManager.cs             # Local AI model file management
│   │   ├── ClipboardHelper.cs          # Clipboard utilities
│   │   ├── BrowserProfileManager.cs    # WebView2 profile management
│   │   ├── BrowserQueryService.cs      # AI browser query handling
│   │   │
│   │   ├── Transcription/              # Speech-to-text services
│   │   │   ├── ITranscriptionService.cs
│   │   │   ├── OpenAITranscriptionService.cs
│   │   │   ├── DeepgramTranscriptionService.cs
│   │   │   ├── DeepgramStreamingService.cs    # Real-time streaming
│   │   │   ├── GroqTranscriptionService.cs
│   │   │   ├── LocalWhisperService.cs         # Whisper.net
│   │   │   ├── FasterWhisperService.cs        # Python-based Whisper
│   │   │   └── FasterWhisperAvailability.cs
│   │   │
│   │   ├── Polish/                     # Text cleanup LLM services
│   │   │   ├── IPolishService.cs
│   │   │   ├── OpenAIPolishService.cs
│   │   │   ├── GroqPolishService.cs
│   │   │   ├── CerebrasPolishService.cs
│   │   │   ├── LocalLLMPolishService.cs       # LLamaSharp
│   │   │   └── DisabledPolishService.cs
│   │   │
│   │   ├── CodeContext/                # IDE context extraction
│   │   │   ├── CodeContextService.cs   # LSP integration, path caching
│   │   │   ├── EnglishDictionary.cs    # Word filtering
│   │   │   ├── PathCache.cs            # File path caching
│   │   │   └── CacheLogger.cs
│   │   │
│   │   └── NoteProviders/              # Note-taking integrations
│   │       ├── INoteProvider.cs
│   │       ├── GoogleTasksNoteProvider.cs
│   │       ├── NotionNoteProvider.cs
│   │       └── NoteProviderRegistry.cs
│   │
│   ├── Assets/Icons/           # Application icons
│   ├── Scripts/                # Python scripts for faster-whisper
│   └── Python/                 # Python integration files
│
└── WisperFlow.Tests/           # Unit tests
    ├── GeminiImageInjectionTests.cs
    ├── HotkeyParserTests.cs
    ├── OpenAIRequestBuilderTests.cs
    └── TextPolisherPromptTests.cs
```

---

## How It Works - Deep Dive

### Core Architecture

The application follows an **event-driven architecture** with `DictationOrchestrator` as the central coordinator:

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│  HotkeyManager  │────▶│  AudioRecorder  │────▶│  Transcription  │
│  (Press/Release)│     │    (NAudio)     │     │   Service       │
└─────────────────┘     └─────────────────┘     └────────┬────────┘
                                                         │
┌─────────────────┐     ┌─────────────────┐     ┌────────▼────────┐
│  TextInjector   │◀────│  PolishService  │◀────│   Raw Text      │
│  (Ctrl+V Paste) │     │   (Optional)    │     │                 │
└─────────────────┘     └─────────────────┘     └─────────────────┘
```

### 1. Hotkey Detection (`HotkeyManager.cs`)

The hotkey system uses **polling with `GetAsyncKeyState`** instead of traditional hotkey registration. This provides:
- Reliable modifier-only detection (e.g., just `Ctrl+Win` without any letter key)
- Press/release detection for push-to-talk
- No conflicts with other applications using the same keys

**How it works:**
```csharp
// Polls every 15ms
private void PollKeyStates(object? state)
{
    // Check all modifier keys
    bool ctrlPressed = IsKeyPressed(VK_LCONTROL) || IsKeyPressed(VK_RCONTROL);
    bool winPressed = IsKeyPressed(VK_LWIN) || IsKeyPressed(VK_RWIN);
    bool altPressed = IsKeyPressed(VK_LMENU) || IsKeyPressed(VK_RMENU);
    bool shiftPressed = IsKeyPressed(VK_LSHIFT) || IsKeyPressed(VK_RSHIFT);
    
    // Match against registered hotkey combinations
    // Fire RecordStart/RecordStop events based on state transitions
}
```

**Priority system for overlapping hotkeys:**
1. **Command Mode** (`Ctrl+Win+Alt`) - 3 keys, starts instantly (most specific)
2. **Regular Dictation** (`Ctrl+Win`) - 2 keys, waits 60ms

The delays prevent false triggers when user is pressing more keys to reach a different mode.

**Release detection** uses a threshold (`ReleaseThresholdMs = 80ms`) to prevent accidental stops from momentary key lifts.

### 2. Audio Recording (`AudioRecorder.cs`)

Uses **NAudio** for microphone capture with these specifics:

- **Format**: 16kHz, 16-bit mono PCM (optimal for Whisper)
- **Buffer**: 50ms chunks for low latency
- **Max duration**: 5 minutes (hardcoded to prevent API cost overruns)
- **Warning**: Shows warning at 4 minutes

**Audio level visualization:**
```csharp
// RMS calculation for audio level (0.0 to 1.0)
private static float CalculateAudioLevel(byte[] buffer, int bytesRecorded)
{
    double sumOfSquares = 0;
    for (int i = 0; i < bytesRecorded - 1; i += 2)
    {
        short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
        double normalizedSample = sample / 32768.0;
        sumOfSquares += normalizedSample * normalizedSample;
    }
    double rms = Math.Sqrt(sumOfSquares / sampleCount);
    return (float)(rms * 3.0); // Scaled for visual feedback
}
```

**Streaming support**: Fires `AudioDataAvailable` event with raw audio chunks for Deepgram real-time transcription.

### 3. Transcription Services

Multiple backends available, all implementing `ITranscriptionService`:

| Service | API | Features |
|---------|-----|----------|
| `OpenAITranscriptionService` | OpenAI Whisper | Batch only, high accuracy |
| `DeepgramTranscriptionService` | Deepgram Nova2 | Batch, keyword boosting |
| `DeepgramStreamingService` | Deepgram Nova2 | Real-time WebSocket streaming |
| `GroqTranscriptionService` | Groq | Fast, affordable |
| `LocalWhisperService` | Whisper.net | Offline, CUDA/OpenVINO support |
| `FasterWhisperService` | Python faster-whisper | Offline, GPU accelerated |

**Deepgram Streaming Flow:**
```csharp
// 1. Open WebSocket connection in background
_streamingService.StartStreamingAsync(language, keywords, ct);

// 2. Audio chunks sent as they arrive
_audioRecorder.AudioDataAvailable += (s, audio) => 
    _streamingService.SendAudioChunkAsync(audio, ct);

// 3. Transcript updates displayed in real-time
_streamingService.OnTranscriptUpdate += (text, isFinal) =>
    _dictationBar.UpdateTranscript(text, isFinal);

// 4. On release, finalize and get complete transcript
var transcript = await _streamingService.StopStreamingAsync(ct);
```

### 4. Text Polishing (LLM Cleanup)

The polish service cleans up raw transcriptions:
- Fixes punctuation and grammar
- Removes filler words ("um", "uh")
- Formats spoken commands ("new paragraph" → actual line break)
- Optional "Notes mode" for structured note-taking

All services implement `IPolishService` with `PolishAsync(transcript, notesMode, ct)`.

### 5. Text Injection (`TextInjector.cs`)

Getting text into the focused application is surprisingly complex due to **clipboard contention**. The app uses a "fast grab" strategy with Win32 APIs:

```csharp
// Fast-grab paste strategy:
// 1. Set clipboard using Win32 API (retries with exponential backoff)
bool setSuccess = SetClipboardWin32(text);

// 2. Send Ctrl+V using keybd_event
keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
keybd_event(VK_V, 0, 0, UIntPtr.Zero);
keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

// 3. Immediately re-grab clipboard to block other apps
for (int grab = 0; grab < 3; grab++)
{
    if (OpenClipboard(IntPtr.Zero))
    {
        await Task.Delay(20);
        CloseClipboard();
        break;
    }
}
```

**Clipboard contention handling:**
- 15 retry attempts with exponential backoff (up to ~1.5s total)
- Logs which process is blocking the clipboard
- Fallback to `SendInput` character-by-character typing (slow but works)

**@ Mention Support for IDEs:**
When text contains `@filename.ext` patterns matching known project files, the injector:
1. Pastes text up to and including `@`
2. Pastes the filename
3. Sends Tab to trigger IDE autocomplete
4. Continues with remaining text

---

## Two Dictation Modes

### Mode 1: Regular Dictation (`Ctrl+Win`)

**Flow**: Voice → Transcribe → Polish → Inject text

```csharp
private async void OnRecordStop(object? sender, EventArgs e)
{
    var audioFilePath = _audioRecorder.StopRecording();
    
    // Transcribe
    var transcript = await _transcriptionService.TranscribeAsync(audioFilePath, language, ct);
    
    // Polish (if enabled)
    if (settings.PolishOutput)
        finalText = await _polishService.PolishAsync(transcript, settings.NotesMode, ct);
    
    // Inject into focused app
    await _textInjector.InjectTextWithMentionsAsync(finalText, fileNames, ct);
}
```

### Mode 2: Command Mode (`Ctrl+Win+Alt`)

Three sub-modes based on context:

| Context | Behavior |
|---------|----------|
| Text selected in text input | **TRANSFORM**: LLM transforms selected text per voice command |
| Text input focused, no selection | **GENERATE**: LLM generates new text from voice command |
| No text input focused | **SEARCH**: Opens AI chatbot with voice query |

```csharp
private async Task ProcessCommandRecordingAsync(string audioFilePath)
{
    var command = await _transcriptionService.TranscribeAsync(audioFilePath, language, ct);
    
    if (!string.IsNullOrWhiteSpace(_commandModeSelectedText))
    {
        // TRANSFORM: Apply command to selected text
        await ProcessTextTransformAsync(_commandModeSelectedText, command, ct);
    }
    else if (_commandModeTextInputFocused)
    {
        // GENERATE: Create new text from command
        await ProcessTextGenerateAsync(command, ct);
    }
    else
    {
        // SEARCH: Open in embedded browser
        await _dictationBar.OpenAndQueryAsync(selectedProvider, command, screenshot);
    }
}
```

**Embedded Browser**: Uses WebView2 with persistent profiles so you stay logged into ChatGPT/Gemini. Queries are injected via JavaScript DOM manipulation for reliability.

---

## Window Management

### DictationBar (`DictationBar.xaml.cs`)

The floating bar at the bottom of the screen:
- **Always-on-top** using `SetWindowPos(HWND_TOPMOST)`
- **Draggable** horizontally with snap-back animation
- **Audio visualization** with animated bars responding to microphone levels
- **State machine** for different UI states (idle, hover, recording, transcribing, error)

**Z-Order Fix for Windows 11 Paint Bug:**
```csharp
// Hook window messages
private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
{
    if (msg == WM_WINDOWPOSCHANGED)
        ForceTopmost();  // Re-enforce topmost on any position change
}

// Also monitor foreground window changes
SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
```

**Layout Architecture:**
The DictationBar uses a Grid-based layout where `MainBar` is always truly centered using `HorizontalAlignment="Center"`. Button panels (`LeftButtonsPanel`, `ProviderButtonsPanel`) are positioned using `RenderTransform.TranslateTransform` which doesn't affect layout measurement. This ensures:
- MainBar stays perfectly centered regardless of button panel sizes
- Smooth animations without layout jumping
- Dynamic positioning that adapts when MainBar expands/contracts or buttons change

The `UpdateButtonPanelPositions()` method calculates offsets based on `MainBar.ActualWidth` and button panel widths. Button show/hide animations are done via code-behind with calculated offsets.

**Screen Coordinate API:**
Use `GetButtonScreenCenter(buttonId)` or `GetButtonScreenBounds(buttonId)` to get screen coordinates of specific buttons. Valid button IDs: "settings", "screenshot", "chatgpt", "gemini", "notion", "tasks".

### FloatingBrowserWindow

WebView2-based embedded browser for ChatGPT/Gemini:
- **Persistent profiles** for maintaining login sessions
- **Pre-initialization** of all providers in background for instant switching
- **JavaScript injection** for query submission (more reliable than clipboard)
- **Screenshot upload** support via file input or ClipboardEvent paste

---

## Service Factory Pattern

`ServiceFactory.cs` creates service instances based on model IDs:

```csharp
public ITranscriptionService CreateTranscriptionService(string modelId)
{
    return modelId switch
    {
        var id when id.StartsWith("openai-") => new OpenAITranscriptionService(...),
        var id when id.StartsWith("deepgram-") => new DeepgramTranscriptionService(...),
        var id when id.StartsWith("groq-") => new GroqTranscriptionService(...),
        var id when id.StartsWith("local-") => new LocalWhisperService(...),
        _ => throw new ArgumentException($"Unknown model: {modelId}")
    };
}
```

---

## API Key Management

### Storage Options

1. **Environment Variables** (development):
   - `OPENAI_API_KEY`
   - `DEEPGRAM_API_KEY`
   - `CEREBRAS_API_KEY`
   - `GROQ_API_KEY`

2. **Windows Credential Manager** (production):
   - Encrypted storage in Windows' secure credential vault
   - Target names: `WisperFlow_OpenAI`, `WisperFlow_Deepgram`, etc.

```csharp
// Save to Credential Manager
CredentialManager.SaveApiKey(apiKey);

// Retrieve (checks Credential Manager, falls back to env var)
var key = CredentialManager.GetApiKey();
```

---

## Settings

Persisted to `%APPDATA%\WisperFlow\settings.json`:

```csharp
public class AppSettings
{
    // Hotkeys
    public HotkeyModifiers HotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Win;
    public HotkeyModifiers CommandHotkeyModifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Win | HotkeyModifiers.Alt;

    // Models
    public string TranscriptionModelId { get; set; } = "openai-whisper";
    public string PolishModelId { get; set; } = "openai-gpt4o-mini";
    public string CommandModeModelId { get; set; } = "openai-gpt4o-mini";

    // Behavior
    public bool PolishOutput { get; set; } = true;
    public bool NotesMode { get; set; } = false;
    public string Language { get; set; } = "auto";

    // Deepgram streaming options
    public bool DeepgramStreaming { get; set; } = false;
    public bool DeepgramSmartFormat { get; set; } = false;
    public bool DeepgramPunctuate { get; set; } = true;
    public int DeepgramEndpointing { get; set; } = 300;  // ms
    // ... many more options
}
```

---

## Development Commands

```powershell
# Build
dotnet build WisperFlow.sln -c Release

# Run
dotnet run --project WisperFlow

# Run tests
dotnet test WisperFlow.Tests

# Publish single-file executable
dotnet publish WisperFlow -c Release -o ./publish
```

---

## Debugging Tips

### Audio Issues
- Check `%APPDATA%\WisperFlow\wisperflow.log` for device info
- Recording saved to Desktop as `wisperflow_debug.wav` for inspection
- Verify microphone permissions in Windows Settings

### Clipboard Issues
- Log shows which process is blocking clipboard
- Falls back to SendInput typing if clipboard unavailable
- Check if target app blocks simulated input

### Hotkey Issues
- Ensure no other app uses same key combination
- Try different modifier combinations in Settings
- Check if running with sufficient privileges

### Window Z-Order Issues
- Known issue with Windows 11 Paint and similar apps
- Currently using WndProc hook + foreground window monitoring as workaround

---

## File Locations

| Item | Location |
|------|----------|
| Settings JSON | `%APPDATA%\WisperFlow\settings.json` |
| API Keys | Windows Credential Manager |
| Logs | `%APPDATA%\WisperFlow\wisperflow.log` |
| Debug Audio | Desktop `wisperflow_debug.wav` |
| WebView2 Profiles | `%LOCALAPPDATA%\WisperFlow\EBWebView` |
| Local Models | `%APPDATA%\WisperFlow\Models\` |

---

## Testing

Unit tests cover:
- Hotkey parsing logic
- OpenAI request building
- Text polisher prompts
- Code dictation generation
- Gemini image injection (browser automation)

```powershell
dotnet test WisperFlow.Tests
```

---

## Key Design Decisions

1. **Polling for hotkeys** instead of RegisterHotKey - allows modifier-only combos and avoids conflicts
2. **Win32 clipboard APIs** instead of WPF Clipboard - better control for "fast grab" strategy
3. **WebView2 for AI chat** instead of external browser - persistent sessions, seamless UX
4. **Multiple backends** for transcription/LLM - flexibility for cost/speed/privacy tradeoffs
5. **Event-driven orchestration** - clean separation of concerns, easy to extend
