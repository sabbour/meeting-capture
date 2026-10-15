using MeetingCapture.Transcription;

namespace MeetingCapture.Graph;

/// <summary>
/// Maps diarization speaker IDs (e.g. "Guest-1", "Guest-2") to resolved human display names.
///
/// Speaker IDs are named only when the user chooses an invite attendee or types a name.
/// Thread-safety: transcript callbacks and UI naming can arrive on different threads.
/// </summary>
public sealed class SpeakerResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The local user's display name when an identity provider supplies it.</summary>
    public string? LocalUserDisplayName { get; private set; }

    /// <summary>Raised the first time a new, unresolved speaker ID is observed.</summary>
    public event EventHandler<string>? UnknownSpeakerDetected;

    /// <summary>Raised whenever a speaker ID is resolved to (or re-assigned) a display name.</summary>
    public event EventHandler<SpeakerResolvedEventArgs>? SpeakerResolved;

    /// <summary>Sets the local user's display name (typically the signed-in user from Graph /me).</summary>
    public void SetLocalUser(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return;

        lock (_gate) LocalUserDisplayName = displayName;
    }

    /// <summary>Manually resolves a speaker ID to a display name (e.g. "Guest-2" → "Rajneesh").</summary>
    public void Resolve(string speakerId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || string.IsNullOrWhiteSpace(displayName))
            return;

        lock (_gate)
        {
            speakerId = CanonicalLocked(speakerId);
            _resolved[speakerId] = displayName;
        }

        SpeakerResolved?.Invoke(this, new SpeakerResolvedEventArgs(speakerId, displayName));
    }

    /// <summary>
    /// Combines the plan's absorbed speakers into its kept speaker: later lookups of an absorbed ID
    /// behave as the kept speaker. Absorbed speakers' own names are dropped.
    /// </summary>
    public void Merge(SpeakerMergePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            var kept = CanonicalLocked(plan.KeptId);
            foreach (var id in plan.AbsorbedIds)
            {
                var absorbed = CanonicalLocked(id);
                if (string.Equals(absorbed, kept, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var alias in _aliases.Where(pair => string.Equals(pair.Value, absorbed,
                    StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
                    _aliases[alias] = kept;
                _aliases[absorbed] = kept;
                _resolved.Remove(absorbed);
                _seen.Add(absorbed);
            }
            _seen.Add(kept);
        }
    }

    /// <summary>Returns the speaker ID that <paramref name="speakerId"/> was combined into, or the ID itself.</summary>
    public string? Canonical(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId)) return speakerId;
        lock (_gate) return CanonicalLocked(speakerId);
    }

    /// <summary>Speaker IDs that were combined into <paramref name="keptId"/>.</summary>
    public IReadOnlyList<string> AliasesOf(string keptId)
    {
        lock (_gate)
            return _aliases.Where(pair => string.Equals(pair.Value, keptId, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToArray();
    }

    private string CanonicalLocked(string speakerId) =>
        _aliases.TryGetValue(speakerId, out var kept) ? kept : speakerId;

    /// <summary>Returns the resolved display name for a speaker ID, or the ID itself if unresolved.</summary>
    public string GetDisplayName(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
            return "Unknown";

        lock (_gate)
        {
            speakerId = CanonicalLocked(speakerId);
            return _resolved.TryGetValue(speakerId, out var name) ? name : speakerId;
        }
    }

    /// <summary>True when a speaker ID already has a resolved display name.</summary>
    public bool IsResolved(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
            return false;

        lock (_gate)
        {
            return _resolved.ContainsKey(CanonicalLocked(speakerId));
        }
    }

    /// <summary>
    /// Called by transcript handlers each time a speaker ID appears. Fires
    /// <see cref="UnknownSpeakerDetected"/> exactly once per unresolved speaker.
    /// </summary>
    public void NotifySpeaker(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
            return;

        bool isNewUnknown;
        lock (_gate)
        {
            speakerId = CanonicalLocked(speakerId);
            if (_resolved.ContainsKey(speakerId))
                return; // already resolved — never prompt
            isNewUnknown = _seen.Add(speakerId);
        }

        if (isNewUnknown)
            UnknownSpeakerDetected?.Invoke(this, speakerId);
    }

    /// <summary>Clears all resolution state for a new recording session.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _resolved.Clear();
            _seen.Clear();
            _aliases.Clear();
            // LocalUserDisplayName is intentionally preserved across sessions (signed-in identity).
        }
    }
}

/// <summary>Carries the speaker ID and its newly resolved display name.</summary>
public sealed class SpeakerResolvedEventArgs(string speakerId, string displayName) : EventArgs
{
    public string SpeakerId { get; } = speakerId;
    public string DisplayName { get; } = displayName;
}
