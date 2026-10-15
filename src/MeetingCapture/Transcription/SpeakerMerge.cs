namespace MeetingCapture.Transcription;

/// <summary>
/// Describes combining several diarized speakers into one: every absorbed speaker ID is rewritten to
/// <see cref="KeptId"/>. Speaker IDs compare case-insensitively, matching the rest of the pipeline.
/// </summary>
public sealed class SpeakerMergePlan
{
    private readonly HashSet<string> _absorbed;

    private SpeakerMergePlan(string keptId, IReadOnlyList<string> absorbedIds)
    {
        KeptId = keptId;
        AbsorbedIds = absorbedIds;
        _absorbed = new HashSet<string>(absorbedIds, StringComparer.OrdinalIgnoreCase);
    }

    public string KeptId { get; }

    public IReadOnlyList<string> AbsorbedIds { get; }

    /// <summary>
    /// Builds a plan that folds <paramref name="speakerIds"/> into <paramref name="keptId"/>.
    /// Returns null when there is nothing to merge (for example merging a speaker into itself).
    /// </summary>
    public static SpeakerMergePlan? Create(IEnumerable<string?> speakerIds, string? keptId)
    {
        ArgumentNullException.ThrowIfNull(speakerIds);
        // Normalize once so IDs differing only by surrounding whitespace or case do not split counts.
        var kept = keptId?.Trim();
        if (string.IsNullOrEmpty(kept)) return null;
        var absorbed = speakerIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Where(id => !string.Equals(id, kept, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return absorbed.Length == 0 ? null : new SpeakerMergePlan(kept, absorbed);
    }

    public bool Absorbs(string? speakerId) =>
        !string.IsNullOrWhiteSpace(speakerId) && _absorbed.Contains(speakerId.Trim());

    /// <summary>Returns the kept ID for an absorbed speaker; any other ID is returned unchanged.</summary>
    public string? Remap(string? speakerId) => Absorbs(speakerId) ? KeptId : speakerId;

    public TranscriptionSegment Remap(TranscriptionSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return Absorbs(segment.SpeakerId) ? segment with { SpeakerId = KeptId } : segment;
    }

    /// <summary>Folds the counts of absorbed speakers into the kept speaker's entry.</summary>
    public void MergeCounts(IDictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        // Remove absorbed entries before updating the destination, which may already have a count.
        var moved = 0;
        foreach (var id in AbsorbedIds)
            if (counts.TryGetValue(id, out var value))
            {
                moved += value;
                counts.Remove(id);
            }
        if (moved == 0) return;
        counts.TryGetValue(KeptId, out var kept);
        counts[KeptId] = kept + moved;
    }
}
