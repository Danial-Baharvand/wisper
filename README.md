# WisperFlow

A lightweight Windows push-to-talk dictation app using OpenAI's Whisper API. Hold a hotkey to record your voice, release to transcribe and insert text into any application.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

- 🎤 **Push-to-talk recording**: Hold Ctrl+Win to record, release to transcribe
- 🤖 **AI-powered transcription**: Uses OpenAI Whisper for accurate speech-to-text
- ✨ **Smart text polishing**: Optional punctuation, grammar, and formatting cleanup
- 📝 **Notes mode**: More aggressive cleanup with bullet points, headings, and structure
- 🌍 **Multi-language support**: 30+ languages supported
- 🔒 **Secure API key storage**: Uses Windows Credential Manager
- 📋 **Universal text injection**: Works with any Windows application
- 🎯 **System tray integration**: Runs quietly in the background

## Quick Start

### Prerequisites

- Windows 10/11
- .NET 8 SDK
- OpenAI API key ([Get one here](https://platform.openai.com/api-keys))

### Installation

1. Clone this repository:
   ```bash
   git clone https://github.com/yourusername/wisperflow.git
   cd wisperflow
   ```

2. Build the application:
   ```bash
   dotnet build WisperFlow.sln -c Release
   ```

3. Run the application:
   ```bash
   dotnet run --project WisperFlow
   ```

### Configure API Key

Set your OpenAI API key using one of these methods:

**Option 1: Environment Variable (Recommended for development)**
```powershell
$env:OPENAI_API_KEY = "sk-your-api-key-here"
```

Or set it permanently:
```powershell
[Environment]::SetEnvironmentVariable("OPENAI_API_KEY", "sk-your-api-key-here", "User")
```

**Option 2: Settings Window**
1. Double-click the WisperFlow tray icon to open Settings
2. Enter your API key in the "OpenAI API Key" section
3. Click "Save" - the key is stored securely in Windows Credential Manager

## Usage

### Basic Dictation

1. **Start recording**: Press and hold `Ctrl + Win`
2. **Speak**: Say what you want to type
3. **Release**: Let go of the hotkey
4. **Done**: Text appears in your focused text field

### Formatting Commands (when Polish is enabled)

Speak these phrases to add formatting:

| Say This | Get This |
|----------|----------|
| "new line" | Line break |
| "new paragraph" | Double line break |
| "comma" | , |
| "period" / "full stop" | . |
| "question mark" | ? |
| "exclamation point" | ! |
| "colon" | : |
| "bullet point [text]" | • [text] |
| "open quote" ... "close quote" | "[text]" |

### Notes Mode (for structured content)

Enable Notes Mode in settings for additional formatting:
- Say "heading: [title]" for bold headings
- Say "bullet point" before items for bullet lists
- Say "number one", "number two" for numbered lists
- Filler words are automatically removed

## Settings

Access settings by double-clicking the tray icon or right-click → Settings.

| Setting | Description |
|---------|-------------|
| **Hotkey** | Key combination to trigger recording (default: Ctrl+Win) |
| **Microphone** | Select input device |
| **Language** | Transcription language (auto-detect or specific) |
| **Polish output** | Enable AI-powered text cleanup |
| **Notes mode** | More aggressive formatting for note-taking |
| **Launch at startup** | Auto-start with Windows |

## Project Structure

```
WisperFlow/
├── WisperFlow/
│   ├── App.xaml(.cs)           # Application entry point
│   ├── OverlayWindow.xaml(.cs) # Recording/transcribing status overlay
│   ├── SettingsWindow.xaml(.cs)# Settings configuration UI
│   ├── Models/
│   │   └── AppSettings.cs      # Settings data model
│   ├── Services/
│   │   ├── HotkeyManager.cs        # Global hotkey detection
│   │   ├── AudioRecorder.cs        # Microphone recording (NAudio)
│   │   ├── OpenAITranscriptionClient.cs # Whisper API client
│   │   ├── TextPolisher.cs         # GPT text cleanup
│   │   ├── TextInjector.cs         # Clipboard paste injection
│   │   ├── CredentialManager.cs    # Secure API key storage
│   │   ├── SettingsManager.cs      # Settings persistence
│   │   ├── TrayIconManager.cs      # System tray integration
│   │   └── DictationOrchestrator.cs# Main workflow coordinator
│   └── Resources/
│       └── app.ico             # Application icon
├── WisperFlow.Tests/           # Unit tests
└── WisperFlow.sln              # Visual Studio solution
```

## Architecture

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│  HotkeyManager  │────▶│  AudioRecorder  │────▶│   OpenAI API    │
│  (Press/Release)│     │    (NAudio)     │     │   (Whisper)     │
└─────────────────┘     └─────────────────┘     └────────┬────────┘
                                                         │
┌─────────────────┐     ┌─────────────────┐     ┌────────▼────────┐
│  TextInjector   │◀────│  TextPolisher   │◀────│   Transcript    │
│  (Ctrl+V Paste) │     │   (GPT-4o-mini) │     │                 │
└─────────────────┘     └─────────────────┘     └─────────────────┘
                                                         │
                        ┌─────────────────┐              │
                        │  OverlayWindow  │◀─────────────┘
                        │   (Status UI)   │
                        └─────────────────┘
```

## Running Tests

```bash
dotnet test WisperFlow.Tests
```

## Troubleshooting

### "No API key found"
- Ensure `OPENAI_API_KEY` environment variable is set, or
- Enter your key in the Settings window

### "Recording too long"
- Whisper API has a 25MB file limit
- Keep recordings under 2 minutes for best results
- The app enforces a 120-second maximum by default

### Text not appearing
- Make sure the target application has focus
- Some applications may block simulated keyboard input
- Try a simple text editor like Notepad to verify

### Hotkey not working
- Check if another application is using the same hotkey
- Try a different key combination in Settings
- Ensure the app is enabled (check tray icon)

## Future Enhancements

- [ ] **Chunked recordings**: Split long recordings for extended dictation
- [ ] **UI Automation**: Direct text insertion via Windows Automation API
- [ ] **Local transcription**: Offline Whisper model support
- [ ] **Realtime streaming**: Live transcription as you speak (when API supports it)
- [ ] **Custom vocabulary**: Domain-specific word lists
- [ ] **Command macros**: Custom voice commands for text snippets
- [ ] **Multi-monitor overlay**: Position overlay on active monitor

## Privacy & Security

- **No audio logging**: Recordings are deleted immediately after transcription
- **Secure storage**: API keys stored in Windows Credential Manager
- **No cloud backend**: All processing via direct OpenAI API calls
- **No telemetry**: No usage data collection

## License

MIT License - see [LICENSE](LICENSE) file for details.

## Acknowledgments

- [OpenAI Whisper](https://openai.com/research/whisper) - Speech recognition
- [NAudio](https://github.com/naudio/NAudio) - Audio capture library
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) - System tray support

