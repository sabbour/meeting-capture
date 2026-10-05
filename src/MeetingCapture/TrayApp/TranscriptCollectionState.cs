using System.Collections.ObjectModel;

namespace MeetingCapture.TrayApp;

internal interface ITranscriptRowState
{
    long Revision { get; set; }
    string SpeakerId { get; set; }
}

internal readonly record struct TranscriptCollectionUpdate<T>(
    T Row,
    bool Added,
    bool Changed,
    bool FinalizedPartial)
{
    internal bool RequiresFollow => Added || FinalizedPartial;
}

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
        var existing = Rows.FirstOrDefault(row => row.Revision == revision);
        if (existing is not null)
            return new(existing, false, update(existing), false);

        if (Partial is not null && Rows.Count > 0 && ReferenceEquals(Rows[^1], Partial))
        {
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
        if (Rows.Count >= capacity)
            Rows.RemoveAt(0);
    }
}
