using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;

namespace WisperFlow.Services;

/// <summary>
/// Manages downloading, storing, and deleting AI models.
/// </summary>
public class ModelManager
{
    private readonly ILogger<ModelManager> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _modelsBasePath;
    
    public event EventHandler<DownloadProgress>? DownloadProgressChanged;

    public ModelManager(ILogger<ModelManager> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        _modelsBasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WisperFlow", "models");
    }

    public string GetModelPath(ModelInfo model)
    {
        var subFolder = model.Type == ModelType.Whisper ? "whisper" : "llm";
        return Path.Combine(_modelsBasePath, subFolder, model.FileName);
    }

    public bool IsModelInstalled(ModelInfo model)
    {
        if (model.Source == ModelSource.OpenAI) return true;
        if (string.IsNullOrEmpty(model.FileName)) return true;
        return File.Exists(GetModelPath(model));
    }

    public long GetInstalledModelsSize()
    {
        if (!Directory.Exists(_modelsBasePath)) return 0;
        return Directory.GetFiles(_modelsBasePath, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    public async Task DownloadModelAsync(ModelInfo model, CancellationToken cancellationToken = default)
    {
        if (model.Source == ModelSource.OpenAI || string.IsNullOrEmpty(model.DownloadUrl))
        {
            _logger.LogWarning("Cannot download model: {Model}", model.Name);
            return;
        }

        var targetPath = GetModelPath(model);
        var targetDir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDir);

        var tempPath = targetPath + ".download";
        var progress = new DownloadProgress { ModelId = model.Id, TotalBytes = model.SizeBytes };

        // Clean up leftover temp file
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

        try
        {
            _logger.LogInformation("Downloading model: {Model} from {Url}", model.Name, model.DownloadUrl);
            
            using var response = await _httpClient.GetAsync(model.DownloadUrl, 
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? model.SizeBytes;
            progress.TotalBytes = totalBytes;

            // Download to temp file in explicit block to ensure closure
            {
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, 
                    FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                var startTime = DateTime.UtcNow;
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;

                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    progress.BytesDownloaded = totalRead;
                    progress.SpeedBytesPerSecond = elapsed > 0 ? totalRead / elapsed : 0;
                    
                    DownloadProgressChanged?.Invoke(this, progress);
                }
                
                await fileStream.FlushAsync(cancellationToken);
            }

            // Move to final location
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            progress.IsComplete = true;
            DownloadProgressChanged?.Invoke(this, progress);
            
            _logger.LogInformation("Model downloaded successfully: {Model}", model.Name);
        }
        catch (OperationCanceledException)
        {
            progress.IsCancelled = true;
            DownloadProgressChanged?.Invoke(this, progress);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            progress.Error = ex.Message;
            DownloadProgressChanged?.Invoke(this, progress);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            _logger.LogError(ex, "Failed to download model: {Model}", model.Name);
            throw;
        }
    }

    public bool DeleteModel(ModelInfo model)
    {
        if (model.Source == ModelSource.OpenAI) return false;
        
        var path = GetModelPath(model);
        if (!File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            _logger.LogInformation("Deleted model: {Model}", model.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete model: {Model}", model.Name);
            return false;
        }
    }
}

