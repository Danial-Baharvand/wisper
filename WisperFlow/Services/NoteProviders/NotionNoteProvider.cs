using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

namespace WisperFlow.Services.NoteProviders;

/// <summary>
/// Notion note provider implementation.
/// Handles OAuth authentication and note creation via Notion API.
/// </summary>
public class NotionNoteProvider : INoteProvider
{
    private const string CREDENTIAL_KEY = "WisperFlow_Notion";
    
    // OAuth credentials for published app
    private const string CLIENT_ID = "2ded872b-594c-8090-bb96-0037f273c547";
    private const string CLIENT_SECRET = "secret_htf4oCL73aHjWzzrktUp5QyzTsfDEMAavKwIBze2gtZ";
    
    private string? _accessToken;
    private string? _workspaceId;
    
    public string Id => "Notion";
    public string DisplayName => "Notion";
    public string IconText => "N";
    public string PrimaryColor => "#000000";
    public string LoginUrl => "https://www.notion.so/login";
    public string DashboardUrl => "https://www.notion.so";
    
    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
    
    public NotionNoteProvider()
    {
        // Try to load existing token
        _accessToken = CredentialManager.GetCredential(CREDENTIAL_KEY);
    }
    
    public string? GetAuthorizationUrl()
    {
        var redirectUri = HttpUtility.UrlEncode("https://localhost/callback");
        return $"https://api.notion.com/v1/oauth/authorize?client_id={CLIENT_ID}&response_type=code&owner=user&redirect_uri={redirectUri}";
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
        // User is on dashboard if URL contains notion.so but is not login/signup
        return url.Contains("notion.so", StringComparison.OrdinalIgnoreCase) &&
               !url.Contains("/login", StringComparison.OrdinalIgnoreCase) &&
               !url.Contains("/signup", StringComparison.OrdinalIgnoreCase) &&
               !url.Contains("api.notion.com", StringComparison.OrdinalIgnoreCase);
    }
    
    public async Task<bool> ExchangeCodeAsync(string code)
    {
        try
        {
            using var client = new HttpClient();
            
            // Notion uses Basic Auth with client_id:client_secret
            var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{CLIENT_ID}:{CLIENT_SECRET}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authString);
            
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = "https://localhost/callback"
            });
            
            var response = await client.PostAsync("https://api.notion.com/v1/oauth/token", content);
            
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            _workspaceId = doc.RootElement.GetProperty("workspace_id").GetString();
            
            // Store token securely
            if (!string.IsNullOrEmpty(_accessToken))
            {
                CredentialManager.SetCredential(CREDENTIAL_KEY, _accessToken);
                return true;
            }
        }
        catch
        {
            // Token exchange failed
        }
        
        return false;
    }
    
    public async Task<bool> CreateNoteAsync(string title, string content)
    {
        if (!IsAuthenticated)
        {
            return false;
        }
        
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            client.DefaultRequestHeaders.Add("Notion-Version", "2022-06-28");
            
            // Create a page in the user's workspace
            // Note: This requires the user to have granted access to a parent page during OAuth
            var requestBody = new
            {
                parent = new { type = "workspace", workspace = true },
                properties = new
                {
                    title = new[]
                    {
                        new { text = new { content = title } }
                    }
                },
                children = new[]
                {
                    new
                    {
                        @object = "block",
                        type = "paragraph",
                        paragraph = new
                        {
                            rich_text = new[]
                            {
                                new { type = "text", text = new { content = content } }
                            }
                        }
                    }
                }
            };
            
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );
            
            var response = await client.PostAsync("https://api.notion.com/v1/pages", jsonContent);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
    
    public void ClearAuth()
    {
        _accessToken = null;
        _workspaceId = null;
        CredentialManager.DeleteCredential(CREDENTIAL_KEY);
    }
}
