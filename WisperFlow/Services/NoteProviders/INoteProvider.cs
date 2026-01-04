namespace WisperFlow.Services.NoteProviders;

/// <summary>
/// Interface for note-taking provider integrations.
/// Implement this interface to add support for new note providers (Notion, OneNote, Motion, etc.)
/// </summary>
public interface INoteProvider
{
    /// <summary>
    /// Unique identifier for this provider (e.g., "Notion", "OneNote").
    /// </summary>
    string Id { get; }
    
    /// <summary>
    /// Display name shown in UI.
    /// </summary>
    string DisplayName { get; }
    
    /// <summary>
    /// Icon character or emoji to display in button.
    /// </summary>
    string IconText { get; }
    
    /// <summary>
    /// Primary color for the provider's UI elements (hex format).
    /// </summary>
    string PrimaryColor { get; }
    
    /// <summary>
    /// Login URL to navigate to on first use.
    /// </summary>
    string LoginUrl { get; }
    
    /// <summary>
    /// Dashboard URL to navigate to after authentication.
    /// </summary>
    string DashboardUrl { get; }
    
    /// <summary>
    /// Whether the user is authenticated with this provider.
    /// </summary>
    bool IsAuthenticated { get; }
    
    /// <summary>
    /// OAuth authorization URL (null if not using OAuth).
    /// </summary>
    string? GetAuthorizationUrl();
    
    /// <summary>
    /// Check if a URL is the OAuth callback redirect.
    /// </summary>
    bool IsCallbackUrl(string url);
    
    /// <summary>
    /// Extract authorization code from callback URL.
    /// </summary>
    string? ExtractAuthCode(string callbackUrl);
    
    /// <summary>
    /// Exchange authorization code for access token.
    /// </summary>
    Task<bool> ExchangeCodeAsync(string code);
    
    /// <summary>
    /// Check if user is on the dashboard (logged in visually).
    /// </summary>
    bool IsDashboardUrl(string url);
    
    /// <summary>
    /// Create a new note with the given title and content.
    /// </summary>
    Task<bool> CreateNoteAsync(string title, string content);
    
    /// <summary>
    /// Clear stored authentication.
    /// </summary>
    void ClearAuth();
}
