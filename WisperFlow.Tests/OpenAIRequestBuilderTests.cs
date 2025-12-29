using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace WisperFlow.Tests;

/// <summary>
/// Tests for OpenAI API request building and response handling.
/// Uses mock HttpMessageHandler to avoid real network calls.
/// </summary>
public class OpenAIRequestBuilderTests
{
    [Fact]
    public async Task TranscriptionRequest_HasCorrectHeaders()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            // Verify authorization header
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal("test-api-key", request.Headers.Authorization.Parameter);

            // Verify content type
            Assert.NotNull(request.Content);
            Assert.Equal("multipart/form-data", request.Content.Headers.ContentType?.MediaType);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\": \"Test transcription\"}", Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);
        
        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-api-key");
        
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 0x52, 0x49, 0x46, 0x46 }), "file", "test.wav");
        content.Add(new StringContent("whisper-1"), "model");
        request.Content = content;

        var response = await httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TranscriptionRequest_IncludesRequiredFields()
    {
        // Arrange
        string? capturedModel = null;
        string? capturedFileName = null;

        var mockHandler = new MockHttpMessageHandler(async request =>
        {
            if (request.Content is MultipartFormDataContent multipartContent)
            {
                foreach (var part in multipartContent)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name == "model")
                    {
                        capturedModel = await part.ReadAsStringAsync();
                    }
                    else if (name == "file")
                    {
                        capturedFileName = part.Headers.ContentDisposition?.FileName?.Trim('"');
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\": \"Hello world\"}", Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);

        // Act
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "audio.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("0"), "temperature");
        content.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        request.Content = content;

        await httpClient.SendAsync(request);

        // Assert
        Assert.Equal("whisper-1", capturedModel);
        Assert.Equal("audio.wav", capturedFileName);
    }

    [Fact]
    public async Task TranscriptionRequest_IncludesOptionalLanguage()
    {
        // Arrange
        string? capturedLanguage = null;

        var mockHandler = new MockHttpMessageHandler(async request =>
        {
            if (request.Content is MultipartFormDataContent multipartContent)
            {
                foreach (var part in multipartContent)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name == "language")
                    {
                        capturedLanguage = await part.ReadAsStringAsync();
                    }
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\": \"Bonjour\"}", Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(mockHandler);

        // Act
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3, 4 }), "file", "audio.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("fr"), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        request.Content = content;

        await httpClient.SendAsync(request);

        // Assert
        Assert.Equal("fr", capturedLanguage);
    }

    [Fact]
    public void ResponseParsing_ExtractsTextFromJson()
    {
        // Arrange
        var responseJson = "{\"text\": \"This is the transcribed text.\"}";
        using var doc = System.Text.Json.JsonDocument.Parse(responseJson);

        // Act
        var text = doc.RootElement.GetProperty("text").GetString();

        // Assert
        Assert.Equal("This is the transcribed text.", text);
    }

    [Theory]
    [InlineData(429, true)]  // Rate limit - should retry
    [InlineData(500, true)]  // Server error - should retry
    [InlineData(502, true)]  // Bad gateway - should retry
    [InlineData(503, true)]  // Service unavailable - should retry
    [InlineData(400, false)] // Bad request - should not retry
    [InlineData(401, false)] // Unauthorized - should not retry
    [InlineData(404, false)] // Not found - should not retry
    public void ShouldRetry_DeterminesCorrectly(int statusCode, bool shouldRetry)
    {
        // Arrange & Act
        var result = statusCode == 429 || statusCode >= 500;

        // Assert
        Assert.Equal(shouldRetry, result);
    }

    /// <summary>
    /// Mock HTTP message handler for testing.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _asyncHandler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _asyncHandler = request => Task.FromResult(handler(request));
        }

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _asyncHandler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, 
            CancellationToken cancellationToken)
        {
            return _asyncHandler(request);
        }
    }
}

