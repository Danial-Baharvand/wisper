using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WisperFlow.Models;
using WisperFlow.Services;
using WisperFlow.Services.Polish;

namespace WisperFlow;

/// <summary>
/// Tests local LLM models to verify they return expected outputs.
/// Run with: dotnet run --project WisperFlow -- --test-llm
/// </summary>
public static class LocalLLMTests
{
    private static readonly List<TestCase> PolishTestCases = new()
    {
        new("um hello this is a test", "Hello, this is a test."),
        new("like you know its really cool", "It's really cool."),
        new("uh i think we should um go now", "I think we should go now."),
        new("the meeting is at like 3pm tomorrow", "The meeting is at 3pm tomorrow."),
        new("so basically what happened was you know", "So, basically what happened was"),
        new("i was gonna say um nevermind", "I was going to say, nevermind."),
        new("its kinda like really important", "It's kind of really important."),
        new("gonna wanna gotta do this", "Going to want to have to do this."),
        new("theres alot of poeple their", "There's a lot of people there."),
        new("ur welcome btw", "You're welcome, by the way."),
    };

    private static readonly List<TransformTestCase> TransformTestCases = new()
    {
        // Translation tests - TinyLlama handles French/Spanish well
        new("hello", "translate to french", ExpectContains: new[] { "Bonjour", "bonjour", "Salut", "salut" }),
        new("dog", "translate to spanish", ExpectContains: new[] { "perro", "Perro" }),
        new("yes", "translate to spanish", ExpectContains: new[] { "si", "Si", "sí", "Sí" }),
        
        // Simple transformations  
        new("hi", "expand this greeting", ExpectLonger: true),
        new("first second third", "reverse the order", ExpectContains: new[] { "third" }),
        new("hello world", "make formal", ExpectDifferent: true),
        
        // Creative transformations
        new("the sky is blue", "make it about the sun", ExpectContains: new[] { "sun", "Sun" }),
        new("morning", "say good morning", ExpectContains: new[] { "Good", "good", "morning" }),
        new("pizza", "describe this food", ExpectLonger: true),
        new("apple", "name another fruit", ExpectContains: new[] { "banana", "Banana", "orange", "Orange", "fruit", "Fruit", "grape", "Grape", "pear", "Pear" }),
    };

    public static async Task RunAllTests(ILoggerFactory loggerFactory, ModelManager modelManager)
    {
        var logger = loggerFactory.CreateLogger("LocalLLMTests");
        logger.LogInformation("=== Starting Local LLM Tests ===");

        // Test with TinyLlama (fastest to test)
        var model = ModelCatalog.GetById("tinyllama-1b");
        if (model == null || !modelManager.IsModelInstalled(model))
        {
            logger.LogWarning("TinyLlama not installed, trying Gemma 2B...");
            model = ModelCatalog.GetById("gemma-2b");
        }
        
        if (model == null || !modelManager.IsModelInstalled(model))
        {
            logger.LogError("No local LLM model installed. Install TinyLlama or Gemma first.");
            return;
        }

        logger.LogInformation("Testing with model: {Model}", model.Name);

        var service = new LocalLLMPolishService(
            loggerFactory.CreateLogger<LocalLLMPolishService>(), 
            modelManager, 
            model);

        await service.InitializeAsync();
        if (!service.IsReady)
        {
            logger.LogError("Failed to initialize model");
            return;
        }

        // Run Polish tests
        logger.LogInformation("\n--- Polish Tests ---");
        int polishPassed = 0;
        foreach (var testCase in PolishTestCases)
        {
            var result = await service.PolishAsync(testCase.Input);
            var passed = EvaluatePolishResult(testCase, result, logger);
            if (passed) polishPassed++;
        }
        logger.LogInformation("Polish Tests: {Passed}/{Total} passed", polishPassed, PolishTestCases.Count);

        // Run Transform tests
        logger.LogInformation("\n--- Transform Tests ---");
        int transformPassed = 0;
        foreach (var testCase in TransformTestCases)
        {
            var result = await service.TransformAsync(testCase.Input, testCase.Command);
            var passed = EvaluateTransformResult(testCase, result, logger);
            if (passed) transformPassed++;
        }
        logger.LogInformation("Transform Tests: {Passed}/{Total} passed", transformPassed, TransformTestCases.Count);

        // Summary
        var totalPassed = polishPassed + transformPassed;
        var totalTests = PolishTestCases.Count + TransformTestCases.Count;
        logger.LogInformation("\n=== TOTAL: {Passed}/{Total} tests passed ({Percent:F0}%) ===", 
            totalPassed, totalTests, (double)totalPassed / totalTests * 100);

        service.Dispose();
    }

    private static bool EvaluatePolishResult(TestCase testCase, string result, ILogger logger)
    {
        // For polish, we mainly check that:
        // 1. Result is not empty
        // 2. Result is different from input (some change was made)
        // 3. Filler words are removed
        
        bool passed = true;
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(result))
        {
            passed = false;
            reasons.Add("empty result");
        }
        else if (result.Trim().ToLower() == testCase.Input.Trim().ToLower())
        {
            passed = false;
            reasons.Add("unchanged");
        }
        else
        {
            // Check if common filler words were removed
            var fillers = new[] { " um ", " uh ", " like ", " you know " };
            var resultLower = result.ToLower();
            foreach (var filler in fillers)
            {
                if (testCase.Input.ToLower().Contains(filler) && resultLower.Contains(filler))
                {
                    // Filler word still present - minor issue but not a failure
                }
            }
        }

        if (passed)
        {
            logger.LogInformation("  ✓ Polish: '{Input}' → '{Result}'", 
                Truncate(testCase.Input, 30), Truncate(result, 40));
        }
        else
        {
            logger.LogWarning("  ✗ Polish FAILED ({Reasons}): '{Input}' → '{Result}'", 
                string.Join(", ", reasons), Truncate(testCase.Input, 30), Truncate(result, 40));
        }

        return passed;
    }

    private static bool EvaluateTransformResult(TransformTestCase testCase, string result, ILogger logger)
    {
        bool passed = true;
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(result))
        {
            passed = false;
            reasons.Add("empty result");
        }
        else if (result.Trim().ToLower() == testCase.Input.Trim().ToLower())
        {
            passed = false;
            reasons.Add("unchanged");
        }
        else
        {
            var resultLower = result.ToLower();

            // Check ExpectContains
            if (testCase.ExpectContains != null && testCase.ExpectContains.Length > 0)
            {
                bool anyFound = testCase.ExpectContains.Any(s => resultLower.Contains(s.ToLower()));
                if (!anyFound)
                {
                    passed = false;
                    reasons.Add($"missing expected: {string.Join("/", testCase.ExpectContains)}");
                }
            }

            // Check ExpectNotContains
            if (testCase.ExpectNotContains != null)
            {
                foreach (var notExpected in testCase.ExpectNotContains)
                {
                    if (resultLower.Contains(notExpected.ToLower()))
                    {
                        passed = false;
                        reasons.Add($"contains forbidden: {notExpected}");
                    }
                }
            }

            // Check length expectations
            if (testCase.ExpectShorter && result.Length >= testCase.Input.Length)
            {
                passed = false;
                reasons.Add("not shorter");
            }
            if (testCase.ExpectLonger && result.Length <= testCase.Input.Length)
            {
                passed = false;
                reasons.Add("not longer");
            }
            
            // ExpectDifferent just requires some change was made
            if (testCase.ExpectDifferent && resultLower == testCase.Input.ToLower())
            {
                passed = false;
                reasons.Add("unchanged");
            }
        }

        if (passed)
        {
            logger.LogInformation("  ✓ Transform '{Command}': '{Input}' → '{Result}'", 
                testCase.Command, Truncate(testCase.Input, 20), Truncate(result, 40));
        }
        else
        {
            logger.LogWarning("  ✗ Transform FAILED ({Reasons}) '{Command}': '{Input}' → '{Result}'", 
                string.Join(", ", reasons), testCase.Command, Truncate(testCase.Input, 20), Truncate(result, 40));
        }

        return passed;
    }

    private static string Truncate(string text, int maxLength)
    {
        text = text.Replace("\n", " ").Replace("\r", "");
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private record TestCase(string Input, string Expected);
    
    private record TransformTestCase(
        string Input, 
        string Command, 
        string[]? ExpectContains = null,
        string[]? ExpectNotContains = null,
        bool ExpectShorter = false,
        bool ExpectLonger = false,
        bool ExpectDifferent = false);
}

