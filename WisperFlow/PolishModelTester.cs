using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services;
using WisperFlow.Services.Polish;

namespace WisperFlow;

/// <summary>
/// Tests all polish models to verify they work correctly.
/// Run from the app by calling PolishModelTester.RunAllTests()
/// </summary>
public static class PolishModelTester
{
    private const string TestText = "this is a test sentence with some grammer mistakes and um filler words like you know";
    private const string TransformCommand = "make this more formal";
    
    public static async Task RunAllTests(ILoggerFactory loggerFactory, ModelManager modelManager)
    {
        var logger = loggerFactory.CreateLogger("PolishModelTester");
        logger.LogInformation("=== Starting Polish Model Tests ===");
        
        var openAiModels = new[] { "openai-gpt5-nano", "openai-gpt5-mini", "openai-gpt4o-mini" };
        var localModels = new[] { "tinyllama-1b", "gemma-2b", "gemma2-2b", "gemma3-1b", "gemma3-4b", "openchat-3.5", "mistral-7b" };
        
        // Test OpenAI models (if API key available)
        foreach (var modelId in openAiModels)
        {
            await TestOpenAIModel(loggerFactory, modelId, logger);
        }
        
        // Test local models (only those that are downloaded)
        foreach (var modelId in localModels)
        {
            var model = ModelCatalog.GetById(modelId);
            if (model != null && modelManager.IsModelInstalled(model))
            {
                await TestLocalModel(loggerFactory, modelManager, model, logger);
            }
            else
            {
                logger.LogWarning("[{Model}] SKIPPED - not downloaded", modelId);
            }
        }
        
        logger.LogInformation("=== Polish Model Tests Complete ===");
    }
    
    private static async Task TestOpenAIModel(ILoggerFactory loggerFactory, string modelId, ILogger logger)
    {
        logger.LogInformation("[{Model}] Testing...", modelId);
        
        try
        {
            var service = new OpenAIPolishService(loggerFactory.CreateLogger<OpenAIPolishService>(), modelId);
            
            if (!service.IsReady)
            {
                logger.LogWarning("[{Model}] SKIPPED - no API key", modelId);
                return;
            }
            
            // Test Polish
            var polished = await service.PolishAsync(TestText);
            if (string.IsNullOrWhiteSpace(polished))
            {
                logger.LogError("[{Model}] FAILED - Polish returned empty", modelId);
            }
            else if (polished == TestText)
            {
                logger.LogWarning("[{Model}] WARNING - Polish returned unchanged text", modelId);
            }
            else
            {
                logger.LogInformation("[{Model}] Polish OK: '{Result}'", modelId, Truncate(polished, 60));
            }
            
            // Test Transform
            var transformed = await service.TransformAsync(TestText, TransformCommand);
            if (string.IsNullOrWhiteSpace(transformed))
            {
                logger.LogError("[{Model}] FAILED - Transform returned empty", modelId);
            }
            else if (transformed == TestText)
            {
                logger.LogWarning("[{Model}] WARNING - Transform returned unchanged text", modelId);
            }
            else
            {
                logger.LogInformation("[{Model}] Transform OK: '{Result}'", modelId, Truncate(transformed, 60));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Model}] FAILED with exception", modelId);
        }
    }
    
    private static async Task TestLocalModel(ILoggerFactory loggerFactory, ModelManager modelManager, ModelInfo model, ILogger logger)
    {
        logger.LogInformation("[{Model}] Testing...", model.Id);
        
        try
        {
            var service = new LocalLLMPolishService(loggerFactory.CreateLogger<LocalLLMPolishService>(), modelManager, model);
            
            await service.InitializeAsync();
            
            if (!service.IsReady)
            {
                logger.LogError("[{Model}] FAILED - could not initialize", model.Id);
                return;
            }
            
            // Test Polish
            var polished = await service.PolishAsync(TestText);
            if (string.IsNullOrWhiteSpace(polished))
            {
                logger.LogError("[{Model}] FAILED - Polish returned empty", model.Id);
            }
            else if (polished == TestText)
            {
                logger.LogWarning("[{Model}] WARNING - Polish returned unchanged text", model.Id);
            }
            else
            {
                logger.LogInformation("[{Model}] Polish OK: '{Result}'", model.Id, Truncate(polished, 60));
            }
            
            // Test Transform  
            var transformed = await service.TransformAsync(TestText, TransformCommand);
            if (string.IsNullOrWhiteSpace(transformed))
            {
                logger.LogError("[{Model}] FAILED - Transform returned empty", model.Id);
            }
            else if (transformed == TestText)
            {
                logger.LogWarning("[{Model}] WARNING - Transform returned unchanged text", model.Id);
            }
            else
            {
                logger.LogInformation("[{Model}] Transform OK: '{Result}'", model.Id, Truncate(transformed, 60));
            }
            
            service.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Model}] FAILED with exception", model.Id);
        }
    }
    
    private static string Truncate(string text, int maxLength)
    {
        text = text.Replace("\n", " ").Replace("\r", "");
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}

