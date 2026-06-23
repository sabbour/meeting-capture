namespace MeetingCapture.Storage;

/// <summary>Persists meeting notes to a configured storage location.</summary>
public interface INotesStorage
{
    /// <summary>
    /// Saves the notes and returns the full path where they were written.
    /// Throws on I/O failure — no silent swallowing.
    /// </summary>
    Task<string> SaveAsync(Intelligence.MeetingNotes notes, CancellationToken cancellationToken);
}
