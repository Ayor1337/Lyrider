using System.Collections.ObjectModel;
using Lyrider.Models;

namespace Lyrider.Services;

internal static class QueueCollectionSynchronizer
{
    public static void Synchronize(
        ObservableCollection<QueueItemInfo> target,
        IReadOnlyList<QueueItemInfo> desiredItems)
    {
        for (var index = 0; index < desiredItems.Count; index++)
        {
            var desiredItem = desiredItems[index];
            if (index < target.Count && HasSameIdentity(target[index], desiredItem))
            {
                UpdateExistingItem(target, index, desiredItem);
                continue;
            }

            var existingIndex = FindIndex(target, desiredItem, index + 1);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
                UpdateExistingItem(target, index, desiredItem);
                continue;
            }

            if (index < target.Count)
            {
                target.Insert(index, desiredItem);
            }
            else
            {
                target.Add(desiredItem);
            }
        }

        while (target.Count > desiredItems.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static int FindIndex(
        ObservableCollection<QueueItemInfo> items,
        QueueItemInfo desiredItem,
        int startIndex)
    {
        for (var index = startIndex; index < items.Count; index++)
        {
            if (HasSameIdentity(items[index], desiredItem))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasSameIdentity(QueueItemInfo left, QueueItemInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.Id) || !string.IsNullOrWhiteSpace(right.Id))
        {
            return string.Equals(left.Id, right.Id, StringComparison.Ordinal);
        }

        return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            string.Equals(left.ArtistName, right.ArtistName, StringComparison.Ordinal) &&
            string.Equals(left.AlbumName, right.AlbumName, StringComparison.Ordinal);
    }

    private static void UpdateExistingItem(
        ObservableCollection<QueueItemInfo> items,
        int index,
        QueueItemInfo desiredItem)
    {
        var existingItem = items[index];
        existingItem.Index = desiredItem.Index;
        if (!HasSamePresentation(existingItem, desiredItem))
        {
            items[index] = desiredItem;
        }
    }

    private static bool HasSamePresentation(QueueItemInfo left, QueueItemInfo right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.ArtistName, right.ArtistName, StringComparison.Ordinal) &&
        string.Equals(left.AlbumName, right.AlbumName, StringComparison.Ordinal) &&
        left.DurationInMillis.Equals(right.DurationInMillis) &&
        string.Equals(left.ArtworkUrl, right.ArtworkUrl, StringComparison.Ordinal);
}
