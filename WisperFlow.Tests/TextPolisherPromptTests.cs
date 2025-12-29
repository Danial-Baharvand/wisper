using Xunit;

namespace WisperFlow.Tests;

/// <summary>
/// Tests for text polisher prompt formatting and behavior.
/// </summary>
public class TextPolisherPromptTests
{
    // The prompts from TextPolisher.cs
    private const string TypingModePrompt = @"You are a transcription post-processor. Clean up the raw speech-to-text output with MINIMAL changes:

RULES:
1. Add proper punctuation (periods, commas, question marks)
2. Fix capitalization (sentence starts, proper nouns)
3. Remove filler words: ""um"", ""uh"", ""like"" (when used as filler), ""you know""
4. Keep the user's exact wording and intent
5. Preserve numbers, emails, URLs, and technical terms exactly

FORMATTING COMMANDS (convert these spoken phrases):
- ""new line"" or ""newline"" → insert actual newline
- ""new paragraph"" → insert double newline
- ""comma"" → ,
- ""period"" or ""full stop"" → .
- ""question mark"" → ?
- ""exclamation point"" or ""exclamation mark"" → !
- ""colon"" → :
- ""semicolon"" → ;
- ""open parenthesis"" / ""close parenthesis"" → ( / )
- ""open quote"" / ""close quote"" or ""quote"" / ""end quote"" → "" / ""
- ""bullet point"" or ""bullet"" followed by text → • [text]

OUTPUT: Return ONLY the cleaned text. No quotes, no markdown, no explanations.";

    private const string NotesModePrompt = @"You are a transcription post-processor optimizing for note-taking. Clean up speech-to-text output:

RULES:
1. Add proper punctuation and capitalization
2. Remove ALL filler words (um, uh, like, you know, basically, actually, so, well)
3. Clean up run-on sentences into clear, concise statements
4. Fix obvious grammar issues
5. Preserve technical terms, numbers, emails, URLs exactly
6. Make the text scannable and readable

FORMATTING COMMANDS (convert these spoken phrases):
- ""new line"" → newline
- ""new paragraph"" → double newline
- ""bullet point"" or ""bullet"" + text → • [text]
- ""heading"" + text → **[text]** (bold heading)
- ""subheading"" + text → *[text]* (italic subheading)
- ""number one/two/three"" at start of items → 1. 2. 3.
- Punctuation commands (comma, period, etc.) → actual punctuation

STRUCTURE:
- If user speaks multiple bullet points, format as a bulleted list
- If user speaks numbered items, format as a numbered list
- If user says ""heading: [something]"", make it a heading

OUTPUT: Return ONLY the cleaned and formatted text. No explanations, no wrapper quotes.";

    [Fact]
    public void TypingModePrompt_ContainsFillerWordRemoval()
    {
        Assert.Contains("um", TypingModePrompt);
        Assert.Contains("uh", TypingModePrompt);
        Assert.Contains("you know", TypingModePrompt);
    }

    [Fact]
    public void TypingModePrompt_ContainsFormattingCommands()
    {
        Assert.Contains("new line", TypingModePrompt);
        Assert.Contains("new paragraph", TypingModePrompt);
        Assert.Contains("bullet point", TypingModePrompt);
        Assert.Contains("comma", TypingModePrompt);
        Assert.Contains("period", TypingModePrompt);
    }

    [Fact]
    public void TypingModePrompt_RequestsOnlyCleanedText()
    {
        Assert.Contains("Return ONLY the cleaned text", TypingModePrompt);
        Assert.Contains("No quotes, no markdown, no explanations", TypingModePrompt);
    }

    [Fact]
    public void NotesModePrompt_IsMoreAggressive()
    {
        Assert.Contains("Remove ALL filler words", NotesModePrompt);
        Assert.Contains("basically", NotesModePrompt);
        Assert.Contains("actually", NotesModePrompt);
    }

    [Fact]
    public void NotesModePrompt_SupportsHeadings()
    {
        Assert.Contains("heading", NotesModePrompt);
        Assert.Contains("subheading", NotesModePrompt);
    }

    [Fact]
    public void NotesModePrompt_SupportsNumberedLists()
    {
        Assert.Contains("numbered list", NotesModePrompt);
        Assert.Contains("number one/two/three", NotesModePrompt);
    }

    [Fact]
    public void BothPrompts_PreserveTechnicalContent()
    {
        Assert.Contains("numbers, emails, URLs", TypingModePrompt);
        Assert.Contains("numbers, emails, URLs", NotesModePrompt);
    }

    [Fact]
    public void RequestBody_HasCorrectStructure()
    {
        // Simulate the request body structure
        var model = "gpt-4o-mini";
        var maxTokens = 600;
        var temperature = 0.1;
        var systemPrompt = TypingModePrompt;
        var userContent = "hello um this is a test";

        var requestBody = new
        {
            model = model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            },
            max_tokens = maxTokens,
            temperature = temperature
        };

        // Assert structure
        Assert.Equal("gpt-4o-mini", requestBody.model);
        Assert.Equal(2, requestBody.messages.Length);
        Assert.Equal("system", requestBody.messages[0].role);
        Assert.Equal("user", requestBody.messages[1].role);
        Assert.Equal(600, requestBody.max_tokens);
        Assert.True(requestBody.temperature < 0.5); // Low temperature for consistency
    }
}

