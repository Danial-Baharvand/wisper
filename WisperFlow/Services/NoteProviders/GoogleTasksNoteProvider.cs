using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services.NoteProviders;

/// <summary>
/// Google Tasks provider implementation.
/// Handles OAuth authentication and task creation via Google Tasks API.
/// </summary>
public class GoogleTasksNoteProvider : INoteProvider
{
    private readonly ILogger<GoogleTasksNoteProvider>? _logger;
    private const string CREDENTIAL_KEY = "WisperFlow_GoogleTasks";
    private const string REFRESH_TOKEN_KEY = "WisperFlow_GoogleTasks_Refresh";
    
    // OAuth credentials - replace with your own from Google Cloud Console
    private const string CLIENT_ID = "69250981298-684ofipii89ejah9n9dfb6a2v8ppvj0m.apps.googleusercontent.com";
    private const string CLIENT_SECRET = "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";
    
    private const string REDIRECT_URI = "https://localhost/callback";
    private const string SCOPES = "https://www.googleapis.com/auth/tasks";
    
    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    
    public string Id => "GoogleTasks";
    public string DisplayName => "Google Tasks";
    public string IconText => "✓";
    public string PrimaryColor => "#4285F4";  // Google Blue
    public string LoginUrl => "https://tasks.google.com";
    public string DashboardUrl => "https://tasks.google.com";
    
    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) || !string.IsNullOrEmpty(_refreshToken);
    
    public GoogleTasksNoteProvider(ILogger<GoogleTasksNoteProvider>? logger = null)
    {
        _logger = logger;
        // Try to load existing tokens
        _accessToken = CredentialManager.GetCredential(CREDENTIAL_KEY);
        _refreshToken = CredentialManager.GetCredential(REFRESH_TOKEN_KEY);
    }
    
    public string? GetAuthorizationUrl()
    {
        var clientId = HttpUtility.UrlEncode(CLIENT_ID);
        var redirectUri = HttpUtility.UrlEncode(REDIRECT_URI);
        var scope = HttpUtility.UrlEncode(SCOPES);
        
        return $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={redirectUri}&response_type=code&scope={scope}&access_type=offline&prompt=consent";
    }
    
    public bool IsCallbackUrl(string url)
    {
        return url.StartsWith("https://localhost/callback", StringComparison.OrdinalIgnoreCase);
    }
    
    public string? ExtractAuthCode(string callbackUrl)
    {
        try
        {
            var uri = new Uri(callbackUrl);
            var query = HttpUtility.ParseQueryString(uri.Query);
            return query["code"];
        }
        catch
        {
            return null;
        }
    }
    
    public bool IsDashboardUrl(string url)
    {
        return url.Contains("tasks.google.com", StringComparison.OrdinalIgnoreCase) ||
               (url.Contains("calendar.google.com", StringComparison.OrdinalIgnoreCase) && 
                url.Contains("tasks", StringComparison.OrdinalIgnoreCase));
    }
    
    public async Task<bool> ExchangeCodeAsync(string code)
    {
        try
        {
            using var client = new HttpClient();
            
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = CLIENT_ID,
                ["client_secret"] = CLIENT_SECRET,
                ["redirect_uri"] = REDIRECT_URI,
                ["grant_type"] = "authorization_code"
            });
            
            var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError($"GoogleTasks: Token exchange failed with status {response.StatusCode}");
                return false;
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            
            if (doc.RootElement.TryGetProperty("refresh_token", out var refreshTokenElement))
            {
                _refreshToken = refreshTokenElement.GetString();
                if (!string.IsNullOrEmpty(_refreshToken))
                {
                    CredentialManager.SetCredential(REFRESH_TOKEN_KEY, _refreshToken);
                }
            }
            
            if (doc.RootElement.TryGetProperty("expires_in", out var expiresIn))
            {
                _tokenExpiry = DateTime.Now.AddSeconds(expiresIn.GetInt32() - 60);
            }
            
            if (!string.IsNullOrEmpty(_accessToken))
            {
                CredentialManager.SetCredential(CREDENTIAL_KEY, _accessToken);
                return true;
            }
        }
        catch (Exception ex)
        {
             _logger?.LogError(ex, "GoogleTasks: Exception during token exchange");
        }
        
        return false;
    }
    
    private async Task<bool> RefreshAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken))
        {
            _logger?.LogWarning("GoogleTasks: Cannot refresh token - no refresh token available");
            return false;
        }
            
        try
        {
            using var client = new HttpClient();
            
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = _refreshToken,
                ["client_id"] = CLIENT_ID,
                ["client_secret"] = CLIENT_SECRET,
                ["grant_type"] = "refresh_token"
            });
            
            var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError($"GoogleTasks: Token refresh failed with status {response.StatusCode}");
                return false;
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            
            if (doc.RootElement.TryGetProperty("expires_in", out var expiresIn))
            {
                _tokenExpiry = DateTime.Now.AddSeconds(expiresIn.GetInt32() - 60);
            }
            
            if (!string.IsNullOrEmpty(_accessToken))
            {
                CredentialManager.SetCredential(CREDENTIAL_KEY, _accessToken);
                return true;
            }
        }
        catch (Exception ex)
        {
             _logger?.LogError(ex, "GoogleTasks: Exception during token refresh");
        }
        
        return false;
    }
    
    private async Task<bool> EnsureValidTokenAsync()
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.Now < _tokenExpiry)
        {
            _logger?.LogInformation($"GoogleTasks: Token still valid until {_tokenExpiry}");
            return true;
        }
        
        _logger?.LogInformation($"GoogleTasks: Token expired or missing, attempting refresh. Has refresh token: {!string.IsNullOrEmpty(_refreshToken)}");
        var result = await RefreshAccessTokenAsync();
        _logger?.LogInformation($"GoogleTasks: Refresh result: {result}");
        return result;
    }
    
    public async Task<bool> CreateNoteAsync(string title, string content)
    {
        _logger?.LogInformation($"GoogleTasks: CreateNoteAsync called with title: {title}");
        _logger?.LogInformation($"GoogleTasks: IsAuthenticated: {IsAuthenticated}, AccessToken: {!string.IsNullOrEmpty(_accessToken)}, RefreshToken: {!string.IsNullOrEmpty(_refreshToken)}");
        
        if (!await EnsureValidTokenAsync())
        {
            _logger?.LogWarning("GoogleTasks: EnsureValidTokenAsync failed");
            return false;
        }
        
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            
            // First get the default task list
            _logger?.LogInformation("GoogleTasks: Fetching task lists...");
            var listsResponse = await client.GetAsync("https://tasks.googleapis.com/tasks/v1/users/@me/lists");
            _logger?.LogInformation($"GoogleTasks: Lists response status: {listsResponse.StatusCode}");
            
            if (!listsResponse.IsSuccessStatusCode)
            {
                var errorContent = await listsResponse.Content.ReadAsStringAsync();
                _logger?.LogError($"GoogleTasks: Lists error: {errorContent}");
                return false;
            }
                
            var listsJson = await listsResponse.Content.ReadAsStringAsync();
            _logger?.LogInformation($"GoogleTasks: Lists response: {(listsJson.Length > 100 ? listsJson.Substring(0, 100) + "..." : listsJson)}");
            using var listsDoc = JsonDocument.Parse(listsJson);
            
            string? taskListId = null;
            if (listsDoc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                taskListId = items[0].GetProperty("id").GetString();
                _logger?.LogInformation($"GoogleTasks: Using task list ID: {taskListId}");
            }
            
            if (string.IsNullOrEmpty(taskListId))
            {
                _logger?.LogError("GoogleTasks: No task lists found");
                return false;
            }
            
            // Create the task
            var taskBody = new
            {
                title = title,
                notes = content
            };
            
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(taskBody),
                Encoding.UTF8,
                "application/json"
            );
            
            _logger?.LogInformation($"GoogleTasks: Creating task in list {taskListId}...");
            var response = await client.PostAsync(
                $"https://tasks.googleapis.com/tasks/v1/lists/{taskListId}/tasks",
                jsonContent
            );
            
            _logger?.LogInformation($"GoogleTasks: Create task response status: {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogError($"GoogleTasks: Create task error: {errorContent}");
            }
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GoogleTasks: Exception in CreateNoteAsync");
            return false;
        }
    }
    
    public void ClearAuth()
    {
        _accessToken = null;
        _refreshToken = null;
        _tokenExpiry = DateTime.MinValue;
        CredentialManager.DeleteCredential(CREDENTIAL_KEY);
        CredentialManager.DeleteCredential(REFRESH_TOKEN_KEY);
    }
}
