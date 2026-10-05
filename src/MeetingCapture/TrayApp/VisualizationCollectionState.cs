using System.Collections.ObjectModel;

namespace MeetingCapture.TrayApp;

internal sealed record VisualizationReconcileResult<T>(
    IReadOnlyList<T> Added,
    IReadOnlyList<T> Changed);

internal static class VisualizationCollectionState
{
    internal static VisualizationReconcileResult<T> Reconcile<T>(
        ObservableCollection<T> current,
        IReadOnlyList<T> incoming,
        Func<T, string> id,
        Func<T, string> fingerprint)
    {
        var incomingIds = incoming.Select(id).ToHashSet(StringComparer.Ordinal);
        for (var index = current.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(id(current[index])))
                current.RemoveAt(index);
        }

        var added = new List<T>();
        var changed = new List<T>();
        for (var targetIndex = 0; targetIndex < incoming.Count; targetIndex++)
        {
            var target = incoming[targetIndex];
            var existingIndex = -1;
            for (var index = targetIndex; index < current.Count; index++)
            {
                if (StringComparer.Ordinal.Equals(id(current[index]), id(target)))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                current.Insert(targetIndex, target);
                added.Add(target);
                continue;
            }

            if (existingIndex != targetIndex)
                current.Move(existingIndex, targetIndex);

            if (!StringComparer.Ordinal.Equals(fingerprint(current[targetIndex]), fingerprint(target)))
            {
                current[targetIndex] = target;
                changed.Add(target);
            }
        }

        return new(added, changed);
    }
}
