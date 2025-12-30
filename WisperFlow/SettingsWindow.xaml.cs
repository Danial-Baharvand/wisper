using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WisperFlow.Models;
using WisperFlow.Services;
using WisperFlow.Services.Transcription;

namespace WisperFlow;

/// <summary>
/// Settings window for configuring WisperFlow with sidebar navigation.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly DictationOrchestrator _orchestrator;
    private readonly AudioRecorder _audioRecorder;
    private readonly ModelManager _modelManager;
    private AppSettings _settings;
    
    // Hotkey capture state
    private bool _isCapturingHotkey;
    private HotkeyModifiers _capturedModifiers;
    private TextBox? _activeHotkeyTextBox;
    private HotkeyModifiers _commandCapturedModifiers;
    private HotkeyModifiers _codeDictationCapturedModifiers;

    // All section panels for navigation
    private readonly Dictionary<string, StackPanel> _sections = new();

    public SettingsWindow(
        SettingsManager settingsManager,
        DictationOrchestrator orchestrator,
        AudioRecorder audioRecorder,
        ModelManager modelManager)
    {
        InitializeComponent();

        _settingsManager = settingsManager;
        _orchestrator = orchestrator;
        _audioRecorder = audioRecorder;
        _modelManager = modelManager;
        _settings = settingsManager.CurrentSettings;

        // Initialize sections dictionary
        _sections["NavGeneral"] = SectionGeneral;
        _sections["NavAudio"] = SectionAudio;
        _sections["NavTranscription"] = SectionTranscription;
        _sections["NavDeepgram"] = SectionDeepgram;
        _sections["NavPolish"] = SectionPolish;
        _sections["NavCodeDictation"] = SectionCodeDictation;
        _sections["NavCommand"] = SectionCommand;
        _sections["NavApiKeys"] = SectionApiKeys;

        LoadSettings();
        PopulateMicrophones();
        PopulateLanguages();
        PopulateModels();
        PopulateSearchEngines();
        PopulateCodeDictationModels();
        PopulateCodeDictationLanguages();
        PopulateDeepgramOptions();
        UpdateApiKeyStatus();
        
        // Set version
        VersionText.Text = "v1.0.0";
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && _sections.ContainsKey(rb.Name))
        {
            // Hide all sections
            foreach (var section in _sections.Values)
            {
                section.Visibility = Visibility.Collapsed;
            }
            
            // Show selected section
            _sections[rb.Name].Visibility = Visibility.Visible;
        }
    }

    private void LoadSettings()
    {
        // Regular dictation hotkey
        _capturedModifiers = _settings.HotkeyModifiers;
        HotkeyTextBox.Text = FormatHotkey(_settings.HotkeyModifiers);

        // Command mode
        CommandModeCheckBox.IsChecked = _settings.CommandModeEnabled;
        CommandHotkeyTextBox.Text = FormatHotkey(_settings.CommandHotkeyModifiers);
        _commandCapturedModifiers = _settings.CommandHotkeyModifiers;
        
        // Code dictation
        CodeDictationCheckBox.IsChecked = _settings.CodeDictationEnabled;
        CodeDictationHotkeyTextBox.Text = FormatHotkey(_settings.CodeDictationHotkeyModifiers);
        _codeDictationCapturedModifiers = _settings.CodeDictationHotkeyModifiers;

        // Polish settings
        PolishCheckBox.IsChecked = _settings.PolishOutput;
        NotesModeCheckBox.IsChecked = _settings.NotesMode;

        // Startup
        StartupCheckBox.IsChecked = _settings.LaunchAtStartup;
        
        // Deepgram settings
        DeepgramStreamingCheckBox.IsChecked = _settings.DeepgramStreaming;
        DeepgramSmartFormatCheckBox.IsChecked = _settings.DeepgramSmartFormat;
        DeepgramPunctuateCheckBox.IsChecked = _settings.DeepgramPunctuate;
        DeepgramDiarizeCheckBox.IsChecked = _settings.DeepgramDiarize;
        DeepgramUtterancesCheckBox.IsChecked = _settings.DeepgramUtterances;
        DeepgramParagraphsCheckBox.IsChecked = _settings.DeepgramParagraphs;
        DeepgramFillerWordsCheckBox.IsChecked = _settings.DeepgramFillerWords;
        DeepgramKeywordsTextBox.Text = _settings.DeepgramKeywords;
        
        // New dictation-optimized settings
        DeepgramDictationCheckBox.IsChecked = _settings.DeepgramDictation;
        DeepgramNumeralsCheckBox.IsChecked = _settings.DeepgramNumerals;
        DeepgramMipOptOutCheckBox.IsChecked = _settings.DeepgramMipOptOut;
        DeepgramRedactTextBox.Text = _settings.DeepgramRedact;
    }

    private void PopulateDeepgramOptions()
    {
        // Profanity filter options
        DeepgramProfanityComboBox.Items.Clear();
        var profanityOptions = new[] 
        { 
            ("false", "Off (no filter)"),
            ("true", "Mask (*** profanity)"),
            ("strict", "Remove (delete profanity)")
        };
        
        foreach (var (value, display) in profanityOptions)
        {
            var item = new ComboBoxItem { Content = display, Tag = value };
            DeepgramProfanityComboBox.Items.Add(item);
            
            if (_settings.DeepgramProfanityFilter == value)
                DeepgramProfanityComboBox.SelectedItem = item;
        }
        
        if (DeepgramProfanityComboBox.SelectedItem == null)
            DeepgramProfanityComboBox.SelectedIndex = 0;
        
        // Endpointing options (for streaming speed)
        DeepgramEndpointingComboBox.Items.Clear();
        var endpointingOptions = new[]
        {
            (0, "Default (auto)"),
            (100, "100ms (very fast)"),
            (200, "200ms (fast)"),
            (300, "300ms (balanced) ✓"),
            (500, "500ms (more accurate)"),
            (1000, "1000ms (most accurate)")
        };
        
        foreach (var (value, display) in endpointingOptions)
        {
            var item = new ComboBoxItem { Content = display, Tag = value };
            DeepgramEndpointingComboBox.Items.Add(item);
            
            if (_settings.DeepgramEndpointing == value)
                DeepgramEndpointingComboBox.SelectedItem = item;
        }
        
        if (DeepgramEndpointingComboBox.SelectedItem == null)
            DeepgramEndpointingComboBox.SelectedIndex = 3; // Default to 300ms
    }

    private void PopulateSearchEngines()
    {
        SearchEngineComboBox.Items.Clear();
        
        foreach (var service in BrowserQueryService.AvailableServices)
        {
            var item = new ComboBoxItem { Content = service, Tag = service };
            SearchEngineComboBox.Items.Add(item);
            
            if (_settings.CommandModeSearchEngine == service)
                SearchEngineComboBox.SelectedItem = item;
        }
        
        if (SearchEngineComboBox.SelectedItem == null)
            SearchEngineComboBox.SelectedIndex = 0;
    }

    private void PopulateMicrophones()
    {
        MicrophoneComboBox.Items.Clear();
        
        // Default device option
        var defaultItem = new ComboBoxItem
        {
            Content = "Default Microphone",
            Tag = "-1"
        };
        MicrophoneComboBox.Items.Add(defaultItem);
        MicrophoneComboBox.SelectedIndex = 0;

        // Available devices
        var devices = _audioRecorder.GetAvailableDevices();
        foreach (var (deviceNumber, name) in devices)
        {
            var item = new ComboBoxItem
            {
                Content = name,
                Tag = deviceNumber.ToString()
            };
            MicrophoneComboBox.Items.Add(item);

            if (_settings.MicrophoneDeviceId == deviceNumber.ToString())
            {
                MicrophoneComboBox.SelectedItem = item;
            }
        }
    }

    private void PopulateLanguages()
    {
        LanguageComboBox.Items.Clear();

        var languages = SettingsManager.GetAvailableLanguages();
        foreach (var (code, name) in languages)
        {
            var item = new ComboBoxItem
            {
                Content = name,
                Tag = code
            };
            LanguageComboBox.Items.Add(item);

            if (_settings.Language == code)
            {
                LanguageComboBox.SelectedItem = item;
            }
        }

        if (LanguageComboBox.SelectedItem == null)
        {
            LanguageComboBox.SelectedIndex = 0;
        }
    }

    private void UpdateApiKeyStatus()
    {
        // OpenAI API Key status
        var hasOpenAIKey = CredentialManager.HasApiKey();
        if (hasOpenAIKey)
        {
            ApiKeyStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0, 212, 170));
            ApiKeyStatusText.Text = "API key configured";
        }
        else
        {
            ApiKeyStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 71, 87));
            ApiKeyStatusText.Text = "No API key found";
        }
        
        // Deepgram API Key status
        var hasDeepgramKey = CredentialManager.HasDeepgramApiKey();
        if (hasDeepgramKey)
        {
            DeepgramApiKeyStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0, 212, 170));
            DeepgramApiKeyStatusText.Text = "API key configured";
        }
        else
        {
            DeepgramApiKeyStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 71, 87));
            DeepgramApiKeyStatusText.Text = "No API key found (required for Deepgram models)";
        }
    }

    private void PopulateModels()
    {
        // Check faster-whisper availability once
        var fasterWhisperAvailable = FasterWhisperAvailability.IsAvailable;
        
        // Transcription models
        TranscriptionModelComboBox.Items.Clear();
        foreach (var model in ModelCatalog.WhisperModels)
        {
            var isFasterWhisper = model.Source == ModelSource.FasterWhisper;
            var isDeepgram = model.Source == ModelSource.Deepgram;
            var installed = _modelManager.IsModelInstalled(model);
            
            string displayName;
            bool isEnabled = true;
            string? tooltip = null;
            
            if (isFasterWhisper && !fasterWhisperAvailable)
            {
                // Grey out faster-whisper models when Python/faster-whisper not available
                displayName = $"{model.Name} (unavailable)";
                isEnabled = false;
                tooltip = FasterWhisperAvailability.UnavailableReason;
            }
            else if (isDeepgram)
            {
                // Deepgram models - check API key
                var hasKey = CredentialManager.HasDeepgramApiKey();
                displayName = hasKey ? model.Name : $"{model.Name} (needs API key)";
                isEnabled = hasKey;
                tooltip = hasKey ? null : "Configure Deepgram API key in API Keys section";
            }
            else if (installed)
            {
                displayName = model.Name;
            }
            else
            {
                displayName = $"{model.Name} ⬇️";
            }
            
            var item = new ComboBoxItem 
            { 
                Content = displayName, 
                Tag = model.Id,
                IsEnabled = isEnabled,
                ToolTip = tooltip
            };
            
            TranscriptionModelComboBox.Items.Add(item);
            
            // Only select if enabled
            if (_settings.TranscriptionModelId == model.Id && isEnabled)
                TranscriptionModelComboBox.SelectedItem = item;
        }
        if (TranscriptionModelComboBox.SelectedItem == null)
        {
            // Select first enabled item
            foreach (ComboBoxItem item in TranscriptionModelComboBox.Items)
            {
                if (item.IsEnabled)
                {
                    TranscriptionModelComboBox.SelectedItem = item;
                    break;
                }
            }
        }
        
        // Polish models
        PolishModelComboBox.Items.Clear();
        foreach (var model in ModelCatalog.LLMModels)
        {
            var installed = _modelManager.IsModelInstalled(model);
            var displayName = installed ? model.Name : $"{model.Name} ⬇️";
            var item = new ComboBoxItem { Content = displayName, Tag = model.Id };
            PolishModelComboBox.Items.Add(item);
            if (_settings.PolishModelId == model.Id)
                PolishModelComboBox.SelectedItem = item;
        }
        if (PolishModelComboBox.SelectedItem == null)
            PolishModelComboBox.SelectedIndex = 0;
        
        // Command Mode models (same LLM models, but separate selection)
        CommandModeModelComboBox.Items.Clear();
        foreach (var model in ModelCatalog.LLMModels)
        {
            // Skip "Disabled" option for Command Mode - it needs a working model
            if (model.Id == "polish-disabled") continue;
            
            var installed = _modelManager.IsModelInstalled(model);
            var displayName = installed ? model.Name : $"{model.Name} ⬇️";
            var item = new ComboBoxItem { Content = displayName, Tag = model.Id };
            CommandModeModelComboBox.Items.Add(item);
            if (_settings.CommandModeModelId == model.Id)
                CommandModeModelComboBox.SelectedItem = item;
        }
        if (CommandModeModelComboBox.SelectedItem == null)
            CommandModeModelComboBox.SelectedIndex = 0;
        
        UpdateModelDescriptions();
    }

    private void UpdateModelDescriptions()
    {
        if (TranscriptionModelComboBox.SelectedItem is ComboBoxItem ti && ti.Tag is string tid)
        {
            var model = ModelCatalog.GetById(tid);
            if (model != null)
            {
                string status;
                if (model.Source == ModelSource.FasterWhisper && !FasterWhisperAvailability.IsAvailable)
                {
                    status = $" ⚠️ {FasterWhisperAvailability.UnavailableReason}";
                }
                else if (model.Source == ModelSource.Deepgram)
                {
                    status = CredentialManager.HasDeepgramApiKey() ? " ✓ Ready" : " ⚠️ API key required";
                }
                else if (!_modelManager.IsModelInstalled(model))
                {
                    status = " (download required)";
                }
                else
                {
                    status = "";
                }
                TranscriptionModelDesc.Text = model.Description + status;
            }
        }
        
        if (PolishModelComboBox.SelectedItem is ComboBoxItem pi && pi.Tag is string pid)
        {
            var model = ModelCatalog.GetById(pid);
            var status = _modelManager.IsModelInstalled(model!) ? "" : " (download required)";
            PolishModelDesc.Text = (model?.Description ?? "") + status;
        }
    }

    private void TranscriptionModel_Changed(object sender, SelectionChangedEventArgs e) => UpdateModelDescriptions();
    private void PolishModel_Changed(object sender, SelectionChangedEventArgs e) => UpdateModelDescriptions();
    private void CodeDictationModel_Changed(object sender, SelectionChangedEventArgs e) => UpdateCodeDictationModelDescription();
    private void CommandModeModel_Changed(object sender, SelectionChangedEventArgs e) => UpdateCommandModeModelDescription();
    
    private void UpdateCommandModeModelDescription()
    {
        // Update model description for command mode if needed (currently no description label)
        // This can be expanded to show model status/requirements
    }
    
    private void PopulateCodeDictationModels()
    {
        CodeDictationModelComboBox.Items.Clear();
        foreach (var model in ModelCatalog.CodeDictationModels)
        {
            var installed = _modelManager.IsModelInstalled(model);
            var displayName = installed ? model.Name : $"{model.Name} ⬇️";
            var item = new ComboBoxItem { Content = displayName, Tag = model.Id };
            CodeDictationModelComboBox.Items.Add(item);
            if (_settings.CodeDictationModelId == model.Id)
                CodeDictationModelComboBox.SelectedItem = item;
        }
        if (CodeDictationModelComboBox.SelectedItem == null)
            CodeDictationModelComboBox.SelectedIndex = 0;
        
        UpdateCodeDictationModelDescription();
    }
    
    private void PopulateCodeDictationLanguages()
    {
        CodeDictationLanguageComboBox.Items.Clear();
        foreach (var (code, name) in ModelCatalog.CodeDictationLanguages)
        {
            var item = new ComboBoxItem { Content = name, Tag = code };
            CodeDictationLanguageComboBox.Items.Add(item);
            if (_settings.CodeDictationLanguage == code)
                CodeDictationLanguageComboBox.SelectedItem = item;
        }
        if (CodeDictationLanguageComboBox.SelectedItem == null)
            CodeDictationLanguageComboBox.SelectedIndex = 0;
    }
    
    private void UpdateCodeDictationModelDescription()
    {
        if (CodeDictationModelComboBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            var model = ModelCatalog.GetById(id);
            var status = _modelManager.IsModelInstalled(model!) ? "" : " (download required)";
            CodeDictationModelDesc.Text = (model?.Description ?? "") + status;
        }
    }

    private void ManageModels_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ModelManagerWindow(_modelManager) { Owner = this };
        if (dialog.ShowDialog() == true)
            PopulateModels();
    }

    private string FormatHotkey(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        
        if (modifiers.HasFlag(HotkeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
            parts.Add("Shift");
        if (modifiers.HasFlag(HotkeyModifiers.Win))
            parts.Add("Win");

        return parts.Count > 0 ? string.Join(" + ", parts) : "None";
    }

    private void HotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        _capturedModifiers = HotkeyModifiers.None;
        _activeHotkeyTextBox = HotkeyTextBox;
        HotkeyTextBox.Text = "Hold modifier keys, then release...";
    }

    private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = false;
        _activeHotkeyTextBox = null;
        
        if (_capturedModifiers == HotkeyModifiers.None)
        {
            HotkeyTextBox.Text = FormatHotkey(_settings.HotkeyModifiers);
        }
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey || _activeHotkeyTextBox != HotkeyTextBox) return;

        e.Handled = true;
        
        // Accumulate modifier keys (don't reset on each keydown)
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            _capturedModifiers |= HotkeyModifiers.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            _capturedModifiers |= HotkeyModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            _capturedModifiers |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            _capturedModifiers |= HotkeyModifiers.Win;

        // Update display while holding
        if (_capturedModifiers != HotkeyModifiers.None)
        {
            HotkeyTextBox.Text = FormatHotkey(_capturedModifiers) + " (release to confirm)";
        }
    }

    private void HotkeyTextBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey || _activeHotkeyTextBox != HotkeyTextBox) return;

        e.Handled = true;

        // Check if ALL modifier keys are now released
        bool anyModifierPressed = 
            Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
            Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) ||
            Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ||
            Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (!anyModifierPressed && _capturedModifiers != HotkeyModifiers.None)
        {
            // All keys released - confirm the selection
            _settings.HotkeyModifiers = _capturedModifiers;
            HotkeyTextBox.Text = FormatHotkey(_capturedModifiers);
            _isCapturingHotkey = false;
            _activeHotkeyTextBox = null;
            Keyboard.ClearFocus();
        }
    }
    
    // ===== Command Mode Hotkey Capture =====
    
    private void CommandHotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        _commandCapturedModifiers = HotkeyModifiers.None;
        _activeHotkeyTextBox = CommandHotkeyTextBox;
        CommandHotkeyTextBox.Text = "Hold modifier keys, then release...";
    }

    private void CommandHotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = false;
        _activeHotkeyTextBox = null;
        
        if (_commandCapturedModifiers == HotkeyModifiers.None)
        {
            CommandHotkeyTextBox.Text = FormatHotkey(_settings.CommandHotkeyModifiers);
        }
    }

    private void CommandHotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey || _activeHotkeyTextBox != CommandHotkeyTextBox) return;

        e.Handled = true;
        
        // Accumulate modifier keys
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            _commandCapturedModifiers |= HotkeyModifiers.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            _commandCapturedModifiers |= HotkeyModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            _commandCapturedModifiers |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            _commandCapturedModifiers |= HotkeyModifiers.Win;

        if (_commandCapturedModifiers != HotkeyModifiers.None)
        {
            CommandHotkeyTextBox.Text = FormatHotkey(_commandCapturedModifiers) + " (release to confirm)";
        }
    }

    private void CommandHotkeyTextBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey || _activeHotkeyTextBox != CommandHotkeyTextBox) return;

        e.Handled = true;

        bool anyModifierPressed = 
            Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
            Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) ||
            Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ||
            Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (!anyModifierPressed && _commandCapturedModifiers != HotkeyModifiers.None)
        {
            _settings.CommandHotkeyModifiers = _commandCapturedModifiers;
            CommandHotkeyTextBox.Text = FormatHotkey(_commandCapturedModifiers);
            _isCapturingHotkey = false;
            _activeHotkeyTextBox = null;
            Keyboard.ClearFocus();
        }
    }
    
    // ===== Code Dictation Hotkey Capture =====
    
    private void CodeDictationHotkeyTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        _codeDictationCapturedModifiers = HotkeyModifiers.None;
        _activeHotkeyTextBox = CodeDictationHotkeyTextBox;
        CodeDictationHotkeyTextBox.Text = "Hold modifier keys, then release...";
    }

    private void CodeDictationHotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = false;
        _activeHotkeyTextBox = null;
        
        if (_codeDictationCapturedModifiers == HotkeyModifiers.None)
        {
            CodeDictationHotkeyTextBox.Text = FormatHotkey(_settings.CodeDictationHotkeyModifiers);
        }
    }

    private void CodeDictationHotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey || _activeHotkeyTextBox != CodeDictationHotkeyTextBox) return;

        e.Handled = true;
        
        // Accumulate modifier keys
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            _codeDictationCapturedModifiers |= HotkeyModifiers.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            _codeDictationCapturedModifiers |= HotkeyModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            _codeDictationCapturedModifiers |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            _codeDictationCapturedModifiers |= HotkeyModifiers.Win;

        if (_codeDictationCapturedModifiers != HotkeyModifiers.None)
        {
            CodeDictationHotkeyTextBox.Text = FormatHotkey(_codeDictationCapturedModifiers) + " (release to confirm)";
        }
    }

    private void CodeDictationHotkeyTextBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey || _activeHotkeyTextBox != CodeDictationHotkeyTextBox) return;

        e.Handled = true;

        bool anyModifierPressed = 
            Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
            Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) ||
            Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ||
            Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (!anyModifierPressed && _codeDictationCapturedModifiers != HotkeyModifiers.None)
        {
            _settings.CodeDictationHotkeyModifiers = _codeDictationCapturedModifiers;
            CodeDictationHotkeyTextBox.Text = FormatHotkey(_codeDictationCapturedModifiers);
            _isCapturingHotkey = false;
            _activeHotkeyTextBox = null;
            Keyboard.ClearFocus();
        }
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyPasswordBox.Password.Trim();
        
        if (string.IsNullOrEmpty(apiKey))
        {
            CredentialManager.DeleteApiKey();
        }
        else
        {
            CredentialManager.SaveApiKey(apiKey);
        }
        
        ApiKeyPasswordBox.Password = "";
        UpdateApiKeyStatus();
        PopulateModels(); // Refresh model availability
        
        MessageBox.Show("OpenAI API key saved securely.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    
    private void SaveDeepgramApiKey_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = DeepgramApiKeyPasswordBox.Password.Trim();
        
        if (string.IsNullOrEmpty(apiKey))
        {
            CredentialManager.DeleteDeepgramApiKey();
        }
        else
        {
            CredentialManager.SaveDeepgramApiKey(apiKey);
        }
        
        DeepgramApiKeyPasswordBox.Password = "";
        UpdateApiKeyStatus();
        PopulateModels(); // Refresh model availability
        
        MessageBox.Show("Deepgram API key saved securely.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Gather all settings
        _settings.PolishOutput = PolishCheckBox.IsChecked ?? false;
        _settings.NotesMode = NotesModeCheckBox.IsChecked ?? false;
        _settings.LaunchAtStartup = StartupCheckBox.IsChecked ?? false;
        
        // Command mode
        _settings.CommandModeEnabled = CommandModeCheckBox.IsChecked ?? true;
        if (SearchEngineComboBox.SelectedItem is ComboBoxItem searchItem)
            _settings.CommandModeSearchEngine = searchItem.Tag?.ToString() ?? "ChatGPT";
        // CommandHotkeyModifiers is already set during capture

        // Code dictation
        _settings.CodeDictationEnabled = CodeDictationCheckBox.IsChecked ?? true;
        if (CodeDictationModelComboBox.SelectedItem is ComboBoxItem codeModelItem)
            _settings.CodeDictationModelId = codeModelItem.Tag?.ToString() ?? "qwen2.5-3b";
        if (CodeDictationLanguageComboBox.SelectedItem is ComboBoxItem codeLangItem)
            _settings.CodeDictationLanguage = codeLangItem.Tag?.ToString() ?? "python";
        // CodeDictationHotkeyModifiers is already set during capture

        // Microphone
        if (MicrophoneComboBox.SelectedItem is ComboBoxItem micItem)
            _settings.MicrophoneDeviceId = micItem.Tag?.ToString() ?? "";

        // Language
        if (LanguageComboBox.SelectedItem is ComboBoxItem langItem)
            _settings.Language = langItem.Tag?.ToString() ?? "auto";

        // Models
        if (TranscriptionModelComboBox.SelectedItem is ComboBoxItem transItem)
            _settings.TranscriptionModelId = transItem.Tag?.ToString() ?? "openai-whisper";
        if (PolishModelComboBox.SelectedItem is ComboBoxItem polishItem)
            _settings.PolishModelId = polishItem.Tag?.ToString() ?? "openai-gpt4o-mini";
        if (CommandModeModelComboBox.SelectedItem is ComboBoxItem cmdItem)
            _settings.CommandModeModelId = cmdItem.Tag?.ToString() ?? "openai-gpt4o-mini";
            
        // Deepgram settings
        _settings.DeepgramStreaming = DeepgramStreamingCheckBox.IsChecked ?? false;
        _settings.DeepgramSmartFormat = DeepgramSmartFormatCheckBox.IsChecked ?? true;
        _settings.DeepgramPunctuate = DeepgramPunctuateCheckBox.IsChecked ?? true;
        _settings.DeepgramDiarize = DeepgramDiarizeCheckBox.IsChecked ?? false;
        _settings.DeepgramUtterances = DeepgramUtterancesCheckBox.IsChecked ?? false;
        _settings.DeepgramParagraphs = DeepgramParagraphsCheckBox.IsChecked ?? false;
        _settings.DeepgramFillerWords = DeepgramFillerWordsCheckBox.IsChecked ?? false;
        _settings.DeepgramKeywords = DeepgramKeywordsTextBox.Text?.Trim() ?? "";
        if (DeepgramProfanityComboBox.SelectedItem is ComboBoxItem profanityItem)
            _settings.DeepgramProfanityFilter = profanityItem.Tag?.ToString() ?? "false";
        
        // New dictation-optimized settings
        _settings.DeepgramDictation = DeepgramDictationCheckBox.IsChecked ?? true;
        _settings.DeepgramNumerals = DeepgramNumeralsCheckBox.IsChecked ?? true;
        _settings.DeepgramMipOptOut = DeepgramMipOptOutCheckBox.IsChecked ?? false;
        _settings.DeepgramRedact = DeepgramRedactTextBox.Text?.Trim() ?? "";
        if (DeepgramEndpointingComboBox.SelectedItem is ComboBoxItem endpointingItem && endpointingItem.Tag is int endpointingValue)
            _settings.DeepgramEndpointing = endpointingValue;

        // Save settings
        _settingsManager.SaveSettings(_settings);

        // Apply to orchestrator
        _orchestrator.ApplySettings(_settings);
        _orchestrator.UpdateHotkey(_settings.HotkeyModifiers, _settings.HotkeyKey);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
    
    private void ViewLogs_Click(object sender, RoutedEventArgs e)
    {
        App.OpenLogFile();
    }
}
