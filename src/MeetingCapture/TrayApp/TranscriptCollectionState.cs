using System.Collections.ObjectModel;

namespace MeetingCapture.TrayApp;

/// <summary>A transcript row that can be updated in place when its revision or speaker changes.</summary>
internal interface ITranscriptRowState
{
    long Revision { get; set; }
    string SpeakerId { get; set; }
}

/// <summary>What an update did to a transcript row: added, changed, or turned a partial into a final line.</summary>
internal readonly record struct TranscriptCollectionUpdate<T>(
    T Row,
    bool Added,
    bool Changed,
    bool FinalizedPartial)
{
    internal bool RequiresFollow => Added || FinalizedPartial;
}

/// <summary>Holds the transcript rows shown in the UI, up to <c>capacity</c>, plus the one in-progress partial row.</summary>
internal sealed class TranscriptCollectionState<T>(int capacity)
    where T : class, ITranscriptRowState
{
    internal ObservableCollection<T> Rows { get; } = [];
    internal T? Partial { get; private set; }

    internal TranscriptCollectionUpdate<T> UpsertPartial(
        string speakerId,
        Func<T> create,
        Func<T, bool> update)
    {
        // A partial belongs to one speaker; replace it when diarization changes instead of relabeling old words.
        if (Partial is not null &&
            !string.Equals(Partial.SpeakerId, speakerId, StringComparison.OrdinalIgnoreCase))
            RemovePartial();

        if (Partial is not null && Rows.Count > 0 && ReferenceEquals(Rows[^1], Partial))
            return new(Partial, false, update(Partial), false);

        TrimForAppend();
        var row = create();
        row.Revision = 0;
        Rows.Add(row);
        Partial = row;
        return new(row, true, true, false);
    }

    internal TranscriptCollectionUpdate<T> ApplyFinal(
        long revision,
        Func<T> create,
        Func<T, bool> update)
    {
        // Update an existing revision first so replayed snapshots do not append duplicate transcript rows.
        var existing = Rows.FirstOrDefault(row => row.Revision == revision);
        if (existing is not null)
            return new(existing, false, update(existing), false);

        if (Partial is not null && Rows.Count > 0 && ReferenceEquals(Rows[^1], Partial))
        {
            // Promote the live row in place so the list does not animate or lay out a duplicate final line.
            var row = Partial;
            Partial = null;
            row.Revision = revision;
            update(row);
            return new(row, false, true, true);
        }

        TrimForAppend();
        var added = create();
        added.Revision = revision;
        Rows.Add(added);
        return new(added, true, true, false);
    }

    internal bool Reconcile<TValue>(
        IReadOnlyList<TValue> values,
        long firstRevision,
        Func<TValue, T> create,
        Func<T, TValue, bool> update)
    {
        // Reuse rows by revision, move them to the incoming order, and remove only rows no longer in the snapshot.
        var changed = Partial is not null;
        RemovePartial();
        for (var index = 0; index < values.Count; index++)
        {
            var revision = firstRevision + index;
            var currentIndex = FindRevision(revision);
            T row;
            if (currentIndex < 0)
            {
                row = create(values[index]);
                row.Revision = revision;
                Rows.Insert(index, row);
                changed = true;
            }
            else
            {
                if (currentIndex != index)
                {
                    Rows.Move(currentIndex, index);
                    changed = true;
                }
                row = Rows[index];
                changed |= update(row, values[index]);
            }
        }

        while (Rows.Count > values.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
            changed = true;
        }
        return changed;
    }

    internal void RemovePartial()
    {
        if (Partial is not null && Rows.Count > 0 && ReferenceEquals(Rows[^1], Partial))
            Rows.RemoveAt(Rows.Count - 1);
        Partial = null;
    }

    internal void Reset()
    {
        Partial = null;
        Rows.Clear();
    }

    private int FindRevision(long revision)
    {
        for (var index = 0; index < Rows.Count; index++)
            if (Rows[index].Revision == revision)
                return index;
        return -1;
    }

    private void TrimForAppend()
    {
        // The collection is a moving window; discard the oldest visible row before adding past capacity.
        if (Rows.Count >= capacity)
            Rows.RemoveAt(0);
    }
}
