using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using LLama;
using LLama.Common;
using WisperFlow.Models;

namespace WisperFlow.Services.Polish;

public class LocalLLMPolishService : IPolishService
{
    private readonly ILogger _logger;
    private readonly ModelManager _modelManager;
    private readonly ModelInfo _model;
    private readonly string? _customTypingPrompt;
    private readonly string? _customNotesPrompt;
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private StatelessExecutor? _executor;  // Stateless for clean inference each time
    private bool _isInitialized;
    private bool _initializationFailed;

    public string ModelId => _model.Id;
    public bool IsReady => _isInitialized && _executor != null;
    
    /// <summary>
    /// Gets the effective typing prompt instruction (custom or default).
    /// </summary>
    public string TypingPromptInstruction => string.IsNullOrWhiteSpace(_customTypingPrompt) 
        ? OpenAIPolishService.DefaultTypingPrompt 
        : _customTypingPrompt;
    
    /// <summary>
    /// Gets the effective notes prompt instruction (custom or default).
    /// </summary>
    public string NotesPromptInstruction => string.IsNullOrWhiteSpace(_customNotesPrompt) 
        ? OpenAIPolishService.DefaultNotesPrompt 
        : _customNotesPrompt;

    public LocalLLMPolishService(ILogger logger, ModelManager modelManager, ModelInfo model, string? customTypingPrompt = null, string? customNotesPrompt = null)
    {
        _logger = logger;
        _modelManager = modelManager;
        _model = model;
        _customTypingPrompt = customTypingPrompt;
        _customNotesPrompt = customNotesPrompt;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;
        if (_initializationFailed)
        {
            _logger.LogWarning("Skipping LLM init - previous attempt failed");
            return; // Don't retry failed models, just return (polish will be skipped)
        }
        
        var modelPath = _modelManager.GetModelPath(_model);
        if (!File.Exists(modelPath))
        {
            _initializationFailed = true;
            _logger.LogError("Model file not found: {Path}", modelPath);
            return; // Don't throw - just skip polish
        }

        _logger.LogInformation("Loading LLM: {Model} ({Size})", _model.Name, _model.SizeFormatted);
        try
        {
            await Task.Run(() =>
            {
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = 2048,  // Increased for longer texts + prompts
                    GpuLayerCount = 0,
                    Threads = Math.Max(1, Environment.ProcessorCount / 2),
                    UseMemorymap = true
                };
                _weights = LLamaWeights.LoadFromFile(parameters);
                _context = _weights.CreateContext(parameters);
                _executor = new StatelessExecutor(_weights, parameters);
            }, cancellationToken);
            _isInitialized = true;
            _logger.LogInformation("LLM loaded successfully");
        }
        catch (Exception ex)
        {
            _initializationFailed = true;
            _logger.LogError(ex, "Failed to load LLM: {Model}. Polish will be skipped.", _model.Name);
            // Don't throw - gracefully degrade to no polish
            // Clean up any partial state
            _executor = null;
            _context?.Dispose();
            _context = null;
            _weights?.Dispose();
            _weights = null;
        }
    }

    private string BuildPrompt(string text, bool notesMode = false)
    {
        // Small models need simpler, more direct prompts
        if (_model.PromptTemplate == "tinyllama")
        {
            return BuildTinyLlamaPrompt(text, notesMode);
        }
        
        if (_model.PromptTemplate == "smollm")
        {
            return BuildSmolLMPrompt(text);
        }
        
        var instruction = notesMode ? NotesPromptInstruction : TypingPromptInstruction;
        
        return _model.PromptTemplate switch
        {
            // Llama 3.x models
            "llama3" => $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n{instruction}<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n{text}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n<result>",
            // Qwen 2.x models (ChatML format)
            "qwen2" => $"<|im_start|>system\n{instruction}<|im_end|>\n<|im_start|>user\n{text}<|im_end|>\n<|im_start|>assistant\n<result>",
            // Qwen 3 models (ChatML with /no_think for speed)
            "qwen3" => $"<|im_start|>system\n{instruction}<|im_end|>\n<|im_start|>user\n/no_think\n{text}<|im_end|>\n<|im_start|>assistant\n<result>",
            // Gemma models
            "gemma" or "gemma2" => $"<start_of_turn>user\n{instruction}\n\nInput: {text}<end_of_turn>\n<start_of_turn>model\n<result>",
            "gemma3" => $"<start_of_turn>user\n{instruction}\n\nInput: {text}<end_of_turn>\n<start_of_turn>model\n<result>",
            // Mistral models
            "mistral" => $"[INST] {instruction}\n\nInput: {text} [/INST]\n<result>",
            // OpenChat
            "openchat" => $"GPT4 Correct User: {instruction}\n\nInput: {text}<|end_of_turn|>GPT4 Correct Assistant: <result>",
            // Default/fallback
            _ => $"### Instruction:\n{instruction}\n\n### Input:\n{text}\n\n### Response:\n<result>"
        };
    }
    
    private static string BuildSmolLMPrompt(string text)
    {
        // SmolLM needs a simpler prompt - no structured output
        return $@"<|im_start|>system
You fix speech transcriptions. Rules:
- Remove filler words (um, uh, like, you know)
- When user corrects themselves (""oh actually"", ""no wait""), keep only the correction
- Remove repeated words (stuttering)
- Fix punctuation and grammar
Output only the cleaned text.<|im_end|>
<|im_start|>user
{text}<|im_end|>
<|im_start|>assistant
";
    }

    private static string BuildTinyLlamaPrompt(string text, bool notesMode)
    {
        // Ultra-simple prompt for TinyLlama - no examples, direct instruction
        return $@"<|system|>
You are a text corrector. Rules:
- Fix grammar and add punctuation
- Remove filler words (um, uh, like, you know)
- When user says ""oh actually"" or ""no wait"", keep only what comes after (the correction)
- Remove repeated words like ""I I went"" → ""I went""
Output only the corrected text.</s>
<|user|>
{text}</s>
<|assistant|>
";
    }

    private static string BuildTypingModeInstruction()
    {
        return @"You are a speech-to-text post-processor. Clean up the transcription intelligently.

SELF-CORRECTIONS - When user corrects themselves, REMOVE the mistake and KEEP the correction:
- ""I went to the oh actually I drove to the store"" → ""I drove to the store""
- ""The meeting is on Monday no wait Tuesday"" → ""The meeting is on Tuesday""
- ""She's twenty oh no thirty years old"" → ""She's thirty years old""
- ""We need apples sorry I meant oranges"" → ""We need oranges""
- Phrases that signal correction: ""oh actually"", ""no actually"", ""no wait"", ""I mean"", ""sorry"", ""oh no"", ""wait""

EDITING COMMANDS - Apply these instructions to the text:
- ""discard last sentence"" / ""delete that"" / ""scratch that"" → remove the preceding sentence
- ""let's go back to the beginning"" / ""start over"" → keep only what comes after
- ""change the word X to Y"" / ""replace X with Y"" → substitute X with Y in the text

STUTTERING & DUPLICATES - Remove repeated words:
- ""I I I went"" → ""I went""
- ""the the meeting"" → ""the meeting""
- ""a a word"" → ""a word""

FORMATTING COMMANDS - Convert spoken formatting to symbols:
- ""new line"" / ""next line"" → actual newline
- ""new paragraph"" → double newline  
- ""open parenthesis"" / ""open paren"" → (
- ""close parenthesis"" / ""close paren"" → )
- ""open bracket"" → [, ""close bracket"" → ]
- ""open quote"" / ""quote"" → "", ""close quote"" / ""end quote"" / ""unquote"" → ""
- ""comma"" → ,  ""period"" / ""full stop"" → .  ""question mark"" → ?
- ""colon"" → :  ""semicolon"" → ;  ""dash"" → -  ""exclamation point"" → !

CLEANUP:
- Add punctuation (periods, commas, question marks)
- Fix capitalization (sentence starts, proper nouns)
- Remove filler words: um, uh, like, you know, basically, so, I mean
- Fix homophones: there/their/they're, your/you're, its/it's
- Fix phonetic mistranscriptions: ""my crow soft"" → ""Microsoft"", ""chat gee pee tee"" → ""ChatGPT""
- Preserve numbers, emails, URLs exactly

OUTPUT: Return ONLY cleaned text inside <result></result> tags. No explanations.";
    }

    private static string BuildNotesModeInstruction()
    {
        return @"You are a speech-to-text post-processor for note-taking. Format the transcription as clean notes.

SELF-CORRECTIONS - Remove the mistake, keep the correction:
- ""The price is fifty oh actually sixty dollars"" → ""The price is sixty dollars""
- ""We need three no make that four items"" → ""We need four items""
- ""First point oh wait let me start over the main idea is"" → ""The main idea is""
- Correction signals: ""oh actually"", ""no wait"", ""I mean"", ""sorry"", ""no actually"", ""let me rephrase""

EDITING COMMANDS:
- ""discard last sentence"" / ""delete that"" / ""scratch that"" → remove preceding sentence
- ""change X to Y"" / ""replace X with Y"" → substitute in the text
- ""go back to the beginning"" / ""start over"" → keep only what follows

FORMATTING COMMANDS:
- ""bullet point"" / ""bullet"" + text → • text
- ""numbered list"" / ""number one"" → 1. format
- ""heading"" + text → **text**
- ""new line"" → newline
- ""new paragraph"" → double newline
- ""open parenthesis"" → (, ""close parenthesis"" → )
- ""open bracket"" → [, ""close bracket"" → ]
- ""open quote"" → "", ""close quote"" / ""unquote"" → ""
- Punctuation commands → actual symbols

CLEANUP:
- Remove ALL filler words (um, uh, like, you know, basically, so, well, I mean, actually)
- Remove stuttering and repeated words
- Fix grammar and make sentences concise
- Fix homophones and phonetic mistranscriptions
- Preserve numbers, emails, URLs

OUTPUT: Return ONLY cleaned text inside <result></result> tags. No explanations.";
    }

    private string[] GetStopTokens()
    {
        return _model.PromptTemplate switch
        {
            // Llama 3.x models
            "llama3" => new[] { "</result>", "<|eot_id|>", "<|start_header_id|>", "<|end_header_id|>" },
            // Qwen 2.x (ChatML format with structured output)
            "qwen2" => new[] { "</result>", "<|im_end|>", "<|im_start|>" },
            // Qwen 3 (ChatML format)
            "qwen3" => new[] { "</result>", "<|im_end|>", "<|im_start|>", "</think>" },
            // SmolLM (ChatML format, no structured output - simpler stops)
            "smollm" => new[] { "<|im_end|>", "<|im_start|>", "<|endoftext|>" },
            // Gemma models
            "gemma" or "gemma2" or "gemma3" => new[] { "</result>", "<end_of_turn>", "<start_of_turn>" },
            // Mistral models
            "mistral" => new[] { "</result>", "</s>", "[INST]" },
            // OpenChat
            "openchat" => new[] { "</result>", "<|end_of_turn|>" },
            // TinyLlama: stop on control tokens and double newlines
            "tinyllama" => new[] { "</s>", "<|user|>", "<|system|>", "\n\n" },
            // Default
            _ => new[] { "</result>", "\n\n\n", "###" }
        };
    }

    private string ExtractResult(string output, string originalText, string? command = null)
    {
        if (string.IsNullOrWhiteSpace(output)) return originalText;
        
        // Remove prompt template markers that may leak into output
        var promptMarkers = new[] { 
            // TinyLlama
            "<|user|>", "<|assistant|>", "<|system|>", "</s>", "<s>",
            // Llama 3.x
            "<|begin_of_text|>", "<|eot_id|>", "<|start_header_id|>", "<|end_header_id|>",
            // Qwen/ChatML
            "<|im_start|>", "<|im_end|>",
            // Gemma
            "<start_of_turn>", "<end_of_turn>",
            // Mistral
            "[INST]", "[/INST]",
            // Generic
            "###"
        };
        foreach (var marker in promptMarkers)
        {
            output = output.Replace(marker, " ");
        }
        
        // Try to extract from <result> tags first
        var resultMatch = Regex.Match(output, @"<result>\s*(.*?)\s*(?:</result>|$)", RegexOptions.Singleline);
        if (resultMatch.Success && !string.IsNullOrWhiteSpace(resultMatch.Groups[1].Value))
        {
            output = resultMatch.Groups[1].Value.Trim();
        }
        
        // Remove any remaining XML-like tags
        output = Regex.Replace(output, @"<[^>]+>", "").Trim();
        
        // Remove command text if it appears in output (model echoing the instruction)
        if (!string.IsNullOrWhiteSpace(command))
        {
            // Check if output starts with or contains the command
            var commandLower = command.ToLower().Trim();
            var outputLower = output.ToLower();
            
            // Remove exact command if it appears at start
            if (outputLower.StartsWith(commandLower))
            {
                output = output[command.Length..].TrimStart(':', ' ', '\n');
            }
            
            // Remove variations like "a [command]:" or "[command] text:"
            var commandPatterns = new[]
            {
                $"a {commandLower}:",
                $"an {commandLower}:",
                $"{commandLower}:",
                $"to {commandLower}:",
                $"how to {commandLower}:",
                $"example of {commandLower}:",
                $"an example of {commandLower}:",
                $"a example of {commandLower}:",
                $"here's a {commandLower}:",
                $"here is a {commandLower}:",
            };
            foreach (var pattern in commandPatterns)
            {
                var idx = outputLower.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx < 50) // Only if near the start
                {
                    output = output[(idx + pattern.Length)..].TrimStart(' ', '\n');
                    break;
                }
            }
        }
        
        // Remove original text if it appears verbatim at the start (model echoing input)
        if (!string.IsNullOrWhiteSpace(originalText) && originalText.Length > 3)
        {
            var originalLower = originalText.ToLower().Trim();
            var outputLower = output.ToLower();
            if (outputLower.StartsWith(originalLower) && output.Length > originalText.Length + 5)
            {
                // Original text at start followed by more content - skip it
                output = output[originalText.Length..].TrimStart(':', ' ', '\n', '.', ',');
            }
        }
        
        // For TinyLlama: Extract the actual content from verbose responses
        if (_model.PromptTemplate == "tinyllama")
        {
            // Common patterns where the result follows a colon
            var colonPatterns = new[] 
            { 
                "here's", "here is", "result:", "output:", "answer:", 
                "corrected:", "fixed:", "revised:", "transformed:",
                "other colors:", "colors:"
            };
            foreach (var pattern in colonPatterns)
            {
                var idx = output.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx < 30)
                {
                    var afterPattern = output[(idx + pattern.Length)..].TrimStart(':', ' ', '\n');
                    // Take until next sentence or newline that looks like an explanation
                    var endIdx = afterPattern.IndexOfAny(new[] { '\n' });
                    if (endIdx > 0 && endIdx < 200)
                    {
                        var extracted = afterPattern[..endIdx].Trim();
                        if (!string.IsNullOrWhiteSpace(extracted) && extracted.Length > 3)
                        {
                            output = extracted;
                            break;
                        }
                    }
                    else if (afterPattern.Length > 3 && afterPattern.Length < 200)
                    {
                        output = afterPattern.Trim();
                        break;
                    }
                }
            }

            // If output still looks like an explanation, try to extract quoted text
            if (output.StartsWith("Sure", StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith("Yes", StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith("I ", StringComparison.OrdinalIgnoreCase))
            {
                // Look for quoted text
                var quoteMatch = Regex.Match(output, "\"([^\"]+)\"");
                if (quoteMatch.Success && quoteMatch.Groups[1].Value.Length > 2)
                {
                    output = quoteMatch.Groups[1].Value;
                }
            }
            
            // Take first meaningful line
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
            {
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    var trimmedLower = trimmed.ToLower();
                    // Skip meta/explanation lines
                    if (trimmed.StartsWith("```") ||
                        trimmedLower.StartsWith("here") ||
                        trimmedLower.StartsWith("sure") ||
                        trimmedLower.StartsWith("yes") ||
                        trimmedLower.StartsWith("i ") ||
                        trimmedLower.StartsWith("the text") ||
                        trimmedLower.StartsWith("to ") ||
                        trimmedLower.StartsWith("a ") ||
                        trimmedLower.StartsWith("an ") ||
                        trimmedLower.StartsWith("other ") ||
                        trimmedLower.Contains("python") ||
                        trimmedLower.Contains("script") ||
                        trimmedLower.Contains("corrector") ||
                        trimmedLower.Contains("example") ||
                        trimmedLower.Contains("version") ||
                        trimmedLower.Contains("transformer") ||
                        trimmedLower.Contains("include") ||
                        trimmedLower.EndsWith(":"))
                    {
                        continue;
                    }
                    if (trimmed.Length > 2)
                    {
                        output = trimmed;
                        break;
                    }
                }
            }
            
            // Final cleanup: remove trailing colons
            output = output.TrimEnd(':', ' ');
            
            // If output is clearly meta/incomplete, return original
            var outputLower = output.ToLower();
            if (outputLower.Contains("text transformer") ||
                outputLower.Contains("example of how") ||
                outputLower.Contains("sample response") ||
                outputLower.Contains("sample output") ||
                outputLower.Equals("here") ||
                output.Length < 3)
            {
                // Clearly meta/incomplete - return original
                output = originalText;
            }
        }
        
        var preambles = new[] {
            "Sure, here is", "Here is the", "Here's the", "The corrected", "Corrected:",
            "Output:", "Result:", "Fixed:", "Cleaned:", "Polished:", "Here you go",
            "The cleaned", "The polished", "The fixed", "I've cleaned", "I've fixed",
            "Here's your", "Your text:", "Cleaned text:", "Fixed text:"
        };
        foreach (var p in preambles)
        {
            var idx = output.IndexOf(p, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < 30)
            {
                var colonIdx = output.IndexOf(':', idx);
                if (colonIdx > 0 && colonIdx < idx + 50)
                    output = output[(colonIdx + 1)..].TrimStart();
            }
        }
        
        output = output.Trim();
        if ((output.StartsWith("\"") && output.EndsWith("\"")) || 
            (output.StartsWith("'") && output.EndsWith("'")))
            output = output[1..^1];
            
        return string.IsNullOrWhiteSpace(output) ? originalText : output.Trim();
    }

    public async Task<string> PolishAsync(string rawText, bool notesMode = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;
        if (_executor == null || _context == null) return rawText;

        _logger.LogInformation("Polishing with {Model}", _model.Name);
        var startTime = DateTime.UtcNow;
        
        try
        {
            var prompt = BuildPrompt(rawText, notesMode);
            var inferenceParams = new InferenceParams 
            { 
                MaxTokens = 300,  // Allow more output for complex corrections
                AntiPrompts = GetStopTokens(),
                SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline 
                { 
                    Temperature = _model.Temperature,
                    RepeatPenalty = _model.RepeatPenalty
                }
            };

            // Run inference completely off the UI thread to prevent freezing
            var resultText = await Task.Run(async () =>
            {
                var result = new StringBuilder();
                await foreach (var token in _executor.InferAsync(prompt, inferenceParams, cancellationToken))
                {
                    result.Append(token);
                    if (result.Length > rawText.Length * 3) break;
                }
                return result.ToString();
            }, cancellationToken);
            
            var polished = ExtractResult(resultText, rawText);
            
            var elapsed = DateTime.UtcNow - startTime;
            _logger.LogInformation("Polish done in {Time:F1}s", elapsed.TotalSeconds);
            
            return polished;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Polish failed");
            return rawText;
        }
    }

    public async Task<string> TransformAsync(string originalText, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originalText)) return originalText;
        if (string.IsNullOrWhiteSpace(command)) return originalText;
        
        if (_executor == null || _context == null)
        {
            _logger.LogWarning("TransformAsync called but LLM not loaded - cannot transform");
            throw new InvalidOperationException("Local LLM not loaded. Please select a different polish model or use OpenAI.");
        }

        _logger.LogInformation("Transforming text with {Model}: {Command}", _model.Name, command);
        var startTime = DateTime.UtcNow;
        
        try
        {
            var prompt = BuildTransformPrompt(originalText, command);
            var inferenceParams = new InferenceParams 
            { 
                MaxTokens = 500,  // Allow longer output for transformations
                AntiPrompts = GetStopTokens(),
                SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline 
                { 
                    Temperature = _model.Temperature,
                    RepeatPenalty = _model.RepeatPenalty
                }
            };

            var resultText = await Task.Run(async () =>
            {
                var result = new StringBuilder();
                await foreach (var token in _executor.InferAsync(prompt, inferenceParams, cancellationToken))
                {
                    result.Append(token);
                    if (result.Length > originalText.Length * 5) break;  // Safety limit
                }
                return result.ToString();
            }, cancellationToken);
            
            _logger.LogDebug("Raw transform output ({Len} chars): {Output}", resultText.Length, 
                resultText.Length > 200 ? resultText[..200] + "..." : resultText);
            
            var transformed = ExtractResult(resultText, originalText, command);
            
            var elapsed = DateTime.UtcNow - startTime;
            _logger.LogInformation("Transform done in {Time:F1}s", elapsed.TotalSeconds);
            
            return transformed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Transform failed");
            return originalText;
        }
    }

    public async Task<string> GenerateAsync(string instruction, byte[]? imageBytes = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return "";
        // Note: imageBytes is accepted but not used - local LLMs don't support multimodal yet
        
        if (_executor == null || _context == null)
        {
            _logger.LogWarning("GenerateAsync called but LLM not loaded - cannot generate");
            throw new InvalidOperationException("Local LLM not loaded. Please select a different polish model or use OpenAI.");
        }

        _logger.LogInformation("Generating text with {Model}: {Instruction}", _model.Name, instruction);
        var startTime = DateTime.UtcNow;
        
        try
        {
            var prompt = BuildGeneratePrompt(instruction);
            var inferenceParams = new InferenceParams 
            { 
                MaxTokens = 1000,  // Allow longer output for generation
                AntiPrompts = GetStopTokens(),
                SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline 
                { 
                    Temperature = _model.Temperature,
                    RepeatPenalty = _model.RepeatPenalty
                }
            };

            var resultText = await Task.Run(async () =>
            {
                var result = new StringBuilder();
                await foreach (var token in _executor.InferAsync(prompt, inferenceParams, cancellationToken))
                {
                    result.Append(token);
                    if (result.Length > 2000) break;  // Safety limit for generation
                }
                return result.ToString();
            }, cancellationToken);
            
            _logger.LogDebug("Raw generate output ({Len} chars): {Output}", resultText.Length, 
                resultText.Length > 200 ? resultText[..200] + "..." : resultText);
            
            var generated = ExtractGeneratedResult(resultText);
            
            var elapsed = DateTime.UtcNow - startTime;
            _logger.LogInformation("Generate done in {Time:F1}s", elapsed.TotalSeconds);
            
            return generated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Generate failed");
            return "";
        }
    }

    private string BuildGeneratePrompt(string instruction)
    {
        // Build a prompt for text generation based on instruction
        var systemPrompt = "You are a helpful writing assistant. Generate text exactly as requested. Output only the requested text with no explanations or preamble.";
        
        return _model.PromptTemplate switch
        {
            "llama3" => $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n{systemPrompt}<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n{instruction}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n",
            "qwen2" => $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{instruction}<|im_end|>\n<|im_start|>assistant\n",
            "qwen3" => $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n/no_think\n{instruction}<|im_end|>\n<|im_start|>assistant\n",
            "smollm" => $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{instruction}<|im_end|>\n<|im_start|>assistant\n",
            "gemma" or "gemma2" => $"<start_of_turn>user\n{instruction}<end_of_turn>\n<start_of_turn>model\n",
            "gemma3" => $"<start_of_turn>user\n{instruction}<end_of_turn>\n<start_of_turn>model\n",
            "mistral" => $"[INST] {instruction} [/INST]\n",
            "openchat" => $"GPT4 Correct User: {instruction}<|end_of_turn|>GPT4 Correct Assistant: ",
            "tinyllama" => $"<|system|>\n{systemPrompt}</s>\n<|user|>\n{instruction}</s>\n<|assistant|>\n",
            _ => $"### Instruction:\n{instruction}\n\n### Response:\n"
        };
    }

    private string ExtractGeneratedResult(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "";
        
        // Remove prompt template markers
        var promptMarkers = new[] { 
            "<|user|>", "<|assistant|>", "<|system|>", "</s>", "<s>",
            "<|begin_of_text|>", "<|eot_id|>", "<|start_header_id|>", "<|end_header_id|>",
            "<|im_start|>", "<|im_end|>",
            "<start_of_turn>", "<end_of_turn>",
            "[INST]", "[/INST]", "###"
        };
        foreach (var marker in promptMarkers)
        {
            output = output.Replace(marker, " ");
        }
        
        // Clean up and return
        output = output.Trim();
        
        // Remove quotes if the whole output is quoted
        if ((output.StartsWith("\"") && output.EndsWith("\"")) || 
            (output.StartsWith("'") && output.EndsWith("'")))
            output = output[1..^1];
        
        return output.Trim();
    }

    private string BuildTransformPrompt(string text, string command)
    {
        // Small models (TinyLlama, SmolLM) need simpler, direct prompts without structured output
        if (_model.PromptTemplate == "tinyllama")
        {
            return BuildTinyLlamaTransformPrompt(text, command);
        }
        
        if (_model.PromptTemplate == "smollm")
        {
            return BuildSmolLMTransformPrompt(text, command);
        }

        var instruction = $@"Transform the text according to this instruction: {command}

RULES:
1. Apply the instruction to transform the text
2. Keep the meaning and key information intact
3. Output ONLY the transformed text inside <result></result> tags
4. No explanations or commentary";
        
        return _model.PromptTemplate switch
        {
            // Llama 3.x models
            "llama3" => $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\nYou are a helpful assistant.<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n{instruction}\n\nOriginal text: {text}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n<result>",
            // Qwen 2.x (ChatML format)
            "qwen2" => $"<|im_start|>system\nYou are a helpful assistant.<|im_end|>\n<|im_start|>user\n{instruction}\n\nOriginal text: {text}<|im_end|>\n<|im_start|>assistant\n<result>",
            // Qwen 3 (ChatML with /no_think for speed)
            "qwen3" => $"<|im_start|>system\nYou are a helpful assistant.<|im_end|>\n<|im_start|>user\n/no_think\n{instruction}\n\nOriginal text: {text}<|im_end|>\n<|im_start|>assistant\n<result>",
            // Gemma models
            "gemma" or "gemma2" => $"<start_of_turn>user\n{instruction}\n\nOriginal text: {text}<end_of_turn>\n<start_of_turn>model\n<result>",
            "gemma3" => $"<start_of_turn>user\n{instruction}\n\nOriginal text: {text}<end_of_turn>\n<start_of_turn>model\n<result>",
            // Mistral models
            "mistral" => $"[INST] {instruction}\n\nOriginal text: {text} [/INST]\n<result>",
            // OpenChat
            "openchat" => $"GPT4 Correct User: {instruction}\n\nOriginal text: {text}<|end_of_turn|>GPT4 Correct Assistant: <result>",
            // Default
            _ => $"### Instruction:\n{instruction}\n\n### Original Text:\n{text}\n\n### Response:\n<result>"
        };
    }
    
    private static string BuildSmolLMTransformPrompt(string text, string command)
    {
        // SmolLM needs a simpler prompt - no structured output tags
        return $@"<|im_start|>system
You rewrite text according to instructions. Output only the rewritten text.<|im_end|>
<|im_start|>user
Instruction: {command}

Text to rewrite:
{text}<|im_end|>
<|im_start|>assistant
";
    }

    private static string BuildTinyLlamaTransformPrompt(string text, string command)
    {
        // Ultra-simple prompt for TinyLlama - direct instruction
        return $@"<|system|>
You are a text transformer. Follow the user's instruction exactly. Output only the transformed text, no explanations.</s>
<|user|>
{command}

{text}</s>
<|assistant|>
";
    }

    public void Dispose()
    {
        _executor = null;
        _context?.Dispose();
        _weights?.Dispose();
        _context = null;
        _weights = null;
        _isInitialized = false;
    }
}
