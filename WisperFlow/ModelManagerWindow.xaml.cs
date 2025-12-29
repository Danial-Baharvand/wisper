using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WisperFlow.Models;
using WisperFlow.Services;

namespace WisperFlow;

public partial class ModelManagerWindow : Window
{
    private readonly ModelManager _modelManager;
    private CancellationTokenSource? _downloadCts;
    private bool _modelsChanged;

    public ModelManagerWindow(ModelManager modelManager)
    {
        InitializeComponent();
        _modelManager = modelManager;
        _modelManager.DownloadProgressChanged += OnDownloadProgress;
        RefreshLists();
        UpdateStorage();
    }

    private void RefreshLists()
    {
        WhisperModelsList.Items.Clear();
        foreach (var m in ModelCatalog.WhisperModels.Where(x => x.Source == ModelSource.Local))
        {
            WhisperModelsList.Items.Add(CreateModelRow(m));
        }

        LLMModelsList.Items.Clear();
        foreach (var m in ModelCatalog.LLMModels.Where(x => x.Source == ModelSource.Local && !string.IsNullOrEmpty(x.FileName)))
        {
            LLMModelsList.Items.Add(CreateModelRow(m));
        }
    }

    private Grid CreateModelRow(ModelInfo model)
    {
        var installed = _modelManager.IsModelInstalled(model);
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
            Fill = installed ? new SolidColorBrush(Color.FromRgb(0, 212, 170)) : new SolidColorBrush(Color.FromRgb(100, 100, 100))
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        info.Children.Add(new TextBlock { Text = model.Name, Foreground = (Brush)FindResource("TextPrimaryBrush"), FontSize = 13 });
        info.Children.Add(new TextBlock { Text = $"{model.SizeFormatted} • {(installed ? "Installed" : "Not installed")}", 
            Foreground = (Brush)FindResource("TextSecondaryBrush"), FontSize = 11 });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var btn = new Button
        {
            Content = installed ? "Delete" : "Download",
            Width = 80, Margin = new Thickness(8, 0, 0, 0),
            Tag = model.Id,
            Style = (Style)FindResource("SecondaryButton")
        };
        btn.Click += ModelButton_Click;
        Grid.SetColumn(btn, 2);
        grid.Children.Add(btn);

        return grid;
    }

    private async void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string modelId) return;
        var model = ModelCatalog.GetById(modelId);
        if (model == null) return;

        if (_modelManager.IsModelInstalled(model))
        {
            if (MessageBox.Show($"Delete {model.Name}?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _modelManager.DeleteModel(model);
                _modelsChanged = true;
                RefreshLists();
                UpdateStorage();
            }
        }
        else
        {
            await DownloadAsync(model);
        }
    }

    private async Task DownloadAsync(ModelInfo model)
    {
        _downloadCts = new CancellationTokenSource();
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadText.Text = $"Downloading {model.Name}...";
        DownloadProgress.Value = 0;

        try
        {
            await _modelManager.DownloadModelAsync(model, _downloadCts.Token);
            _modelsChanged = true;
            RefreshLists();
            UpdateStorage();
            MessageBox.Show($"{model.Name} downloaded!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Download failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void OnDownloadProgress(object? sender, DownloadProgress e)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value = e.ProgressPercent;
            var mb = e.BytesDownloaded / 1_000_000.0;
            var total = e.TotalBytes / 1_000_000.0;
            var speed = e.SpeedBytesPerSecond / 1_000_000.0;
            DownloadStatus.Text = $"{mb:F0} / {total:F0} MB ({speed:F1} MB/s)";
        });
    }

    private void UpdateStorage()
    {
        var size = _modelManager.GetInstalledModelsSize();
        StorageText.Text = size >= 1_000_000_000 
            ? $"Storage: {size / 1_000_000_000.0:F1} GB"
            : $"Storage: {size / 1_000_000.0:F0} MB";
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = _modelsChanged; Close(); }

    protected override void OnClosed(EventArgs e)
    {
        _modelManager.DownloadProgressChanged -= OnDownloadProgress;
        _downloadCts?.Cancel();
        base.OnClosed(e);
    }
}

