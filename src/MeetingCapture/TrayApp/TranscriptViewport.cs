namespace MeetingCapture.TrayApp;

public sealed class TranscriptViewport
{
    public const int Capacity = 500;
    public int Start { get; private set; }
    public bool FollowLatest { get; private set; } = true;

    public (int Start, int Count) Update(int total)
    {
        if (FollowLatest) Start = Math.Max(0, total - Capacity);
        Start = Math.Clamp(Start, 0, Math.Max(0, total - 1));
        return (Start, Math.Min(Capacity, total - Start));
    }

    public void Earlier() { FollowLatest = false; Start = Math.Max(0, Start - Capacity); }
    public void Later(int total) { FollowLatest = false; Start = Math.Min(Math.Max(0, total - Capacity), Start + Capacity); }
    public void Pause() => FollowLatest = false;
    public void Latest() => FollowLatest = true;
}
