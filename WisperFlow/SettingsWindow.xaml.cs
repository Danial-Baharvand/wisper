using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WisperFlow.Models;
using WisperFlow.Services;

namespace WisperFlow;

/// <summary>
/// Settings window for configuring WisperFlow.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsManager _settingsManager;
    private readonly DictationOrchestrator _orchestrator;
    private readonly AudioRecorder _audioRecorder;
    private readonly ModelManager _modelManager;
    private AppSettings _settings;
    private bool _isCapturingHotkey;
    private HotkeyModifiers _capturedModifiers;

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

        LoadSettings();
        PopulateMicrophones();
        PopulateLanguages();
        PopulateModels();
        UpdateApiKeyStatus();
    }

    private void LoadSettings()
    {
        // Hotkey
        HotkeyTextBox.Text = FormatHotkey(_settings.HotkeyModifiers);

        // Polish settings
        PolishCheckBox.IsChecked = _settings.PolishOutput;
        NotesModeCheckBox.IsChecked = _settings.NotesMode;

        // Startup
        StartupCheckBox.IsChecked = _settings.LaunchAtStartup;
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
        var hasKey = CredentialManager.HasApiKey();
        
        if (hasKey)
        {
            ApiKeyStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0, 212, 170));
            ApiKeyStatusText.Text = "API key configured";
        }
        else
        {
            ApiKeyStatusDot.Fill = new SolidColorBrush(Color.FromRgb(255, 71, 87));
            ApiKeyStatusText.Text = "No API key found";
        }
    }

    private void PopulateModels()
    {
        // Transcription models
        TranscriptionModelComboBox.Items.Clear();
        foreach (var model in ModelCatalog.WhisperModels)
        {
            var installed = _modelManager.IsModelInstalled(model);
            var displayName = installed ? model.Name : $"{model.Name} ⬇️";
            var item = new ComboBoxItem { Content = displayName, Tag = model.Id };
            TranscriptionModelComboBox.Items.Add(item);
            if (_settings.TranscriptionModelId == model.Id)
                TranscriptionModelComboBox.SelectedItem = item;
        }
        if (TranscriptionModelComboBox.SelectedItem == null)
            TranscriptionModelComboBox.SelectedIndex = 0;
        
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
        
        UpdateModelDescriptions();
    }

    private void UpdateModelDescriptions()
    {
        if (TranscriptionModelComboBox.SelectedItem is ComboBoxItem ti && ti.Tag is string tid)
        {
            var model = ModelCatalog.GetById(tid);
            var status = _modelManager.IsModelInstalled(model!) ? "" : " (download required)";
            TranscriptionModelDesc.Text = (model?.Description ?? "") + status;
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
        HotkeyTextBox.Text = "Press modifier keys...";
    }

    private void HotkeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = false;
        
        if (_capturedModifiers == HotkeyModifiers.None)
        {
            HotkeyTextBox.Text = FormatHotkey(_settings.HotkeyModifiers);
        }
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingHotkey) return;

        e.Handled = true;
        _capturedModifiers = HotkeyModifiers.None;

        // Check modifier keys
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            _capturedModifiers |= HotkeyModifiers.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            _capturedModifiers |= HotkeyModifiers.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            _capturedModifiers |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            _capturedModifiers |= HotkeyModifiers.Win;

        if (_capturedModifiers != HotkeyModifiers.None)
        {
            HotkeyTextBox.Text = FormatHotkey(_capturedModifiers);
            _settings.HotkeyModifiers = _capturedModifiers;
            
            // Clear focus to confirm
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
        
        MessageBox.Show("API key saved securely.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Gather all settings
        _settings.PolishOutput = PolishCheckBox.IsChecked ?? false;
        _settings.NotesMode = NotesModeCheckBox.IsChecked ?? false;
        _settings.LaunchAtStartup = StartupCheckBox.IsChecked ?? false;

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
}

