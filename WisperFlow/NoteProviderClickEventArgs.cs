namespace WisperFlow;

/// <summary>
/// Event arguments for note provider button clicks.
/// </summary>
public class NoteProviderClickEventArgs : EventArgs
{
    /// <summary>
    /// The ID of the note provider that was clicked (e.g., "Notion").
    /// </summary>
    public string ProviderId { get; }
    
    /// <summary>
    /// Whether the click occurred during active recording.
    /// If true, this signals intent to create a note after transcription.
    /// </summary>
    public bool DuringRecording { get; }
    
    public NoteProviderClickEventArgs(string providerId, bool duringRecording)
    {
        ProviderId = providerId;
        DuringRecording = duringRecording;
    }
}
