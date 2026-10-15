namespace MeetingCapture.TrayApp;

/// <summary>A window of at most <see cref="Capacity"/> transcript rows to show. It follows the newest rows until the user pages away.</summary>
public sealed class TranscriptViewport
{
    /// <summary>Largest number of rows shown at once.</summary>
    public const int Capacity = 500;

    /// <summary>Index of the first visible row.</summary>
    public int Start { get; private set; }

    /// <summary>True while the window tracks the newest rows.</summary>
    public bool FollowLatest { get; private set; } = true;

    /// <summary>Recomputes the visible range for a transcript with <paramref name="total"/> rows.</summary>
    public (int Start, int Count) Update(int total)
    {
        // FollowLatest tracks the tail; a paused viewport keeps its requested page inside the new transcript bounds.
        if (FollowLatest) Start = Math.Max(0, total - Capacity);
        Start = Math.Clamp(Start, 0, Math.Max(0, total - 1));
        return (Start, Math.Min(Capacity, total - Start));
    }

    /// <summary>Moves one page back and stops following the newest rows.</summary>
    public void Earlier() { FollowLatest = false; Start = Math.Max(0, Start - Capacity); }
    /// <summary>Moves one page forward and stops following the newest rows.</summary>
    public void Later(int total) { FollowLatest = false; Start = Math.Min(Math.Max(0, total - Capacity), Start + Capacity); }
    /// <summary>Stops following the newest rows.</summary>
    public void Pause() => FollowLatest = false;

    /// <summary>Follows the newest rows again.</summary>
    public void Latest() => FollowLatest = true;
}
