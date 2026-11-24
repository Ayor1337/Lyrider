using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using Lyrider.Models;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class QueueRegressionTests
{
    [TestMethod]
    public void Synchronize_UnchangedQueue_DoesNotRaiseCollectionChanges()
    {
        var originalItem = CreateItem("1", "Song");
        var target = new ObservableCollection<QueueItemInfo> { originalItem };
        var changes = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => changes.Add(args.Action);

        QueueCollectionSynchronizer.Synchronize(target, [CreateItem("1", "Song")]);

        Assert.AreEqual(0, changes.Count);
        Assert.AreSame(originalItem, target[0]);
    }

    [TestMethod]
    public void Synchronize_IndexOnlyChange_UpdatesIndexWithoutCollectionChanges()
    {
        var originalItem = CreateItem("1", "Song");
        var target = new ObservableCollection<QueueItemInfo> { originalItem };
        var changes = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => changes.Add(args.Action);
        var desiredItem = CreateItem("1", "Song");
        desiredItem.Index = 8;

        QueueCollectionSynchronizer.Synchronize(target, [desiredItem]);

        Assert.AreEqual(0, changes.Count);
        Assert.AreSame(originalItem, target[0]);
        Assert.AreEqual(8, target[0].Index);
    }

    [DataTestMethod]
    [DataRow("en-US", "Song, Artist, 3:00")]
    [DataRow("zh-CN", "Song，Artist，3:00")]
    public void AccessibleName_UiCulture_ListsNameArtistAndDuration(string culture, string expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            Assert.AreEqual(expected, CreateItem("1", "Song").AccessibleName);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [DataTestMethod]
    [DataRow(true, true, "Song, Artist, 3:00, synced lyrics")]
    [DataRow(true, false, "Song, Artist, 3:00, plain lyrics")]
    public void AccessibleName_WithLyrics_AnnouncesLyricsKind(bool hasLyrics, bool synced, string expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            var item = new QueueItemInfo(0, "1", "Song", "Artist", "Album", 180_000, null, hasLyrics, synced);

            Assert.AreEqual(expected, item.AccessibleName);
            Assert.AreEqual(hasLyrics && !synced, item.HasPlainLyricsOnly);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [DataTestMethod]
    [DataRow("Album", "Artist · Album")]
    [DataRow("—", "Artist")]
    [DataRow("", "Artist")]
    public void SubtitleText_AlbumName_JoinsArtistAndKnownAlbum(string album, string expected)
    {
        var item = new QueueItemInfo(0, "1", "Song", "Artist", album, 180_000, null);

        Assert.AreEqual(expected, item.SubtitleText);
    }

    [DataTestMethod]
    [DataRow("zh-CN", 0, 0, "0 首")]
    [DataRow("zh-CN", 6, 230_000, "6 首 · 约 23 分钟")]
    [DataRow("zh-CN", 20, 195_000, "20 首 · 约 1 小时 5 分钟")]
    [DataRow("en-US", 1, 180_000, "1 song · about 3 min")]
    [DataRow("en-US", 20, 180_000, "20 songs · about 1 hr")]
    public void QueueSummary_Items_ShowsCountAndRoundedDuration(
        string culture,
        int count,
        double millisEach,
        string expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            var items = Enumerable.Range(0, count)
                .Select(index => new QueueItemInfo(index, $"{index}", "Song", "Artist", "Album", millisEach, null))
                .ToArray();

            Assert.AreEqual(expected, QueueSummary.Format(items));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [TestMethod]
    public void IsSwitching_Changed_NotifiesStateAndDimsContent()
    {
        var item = CreateItem("1", "Song");
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.IsSwitching = true;
        item.IsSwitching = true;

        CollectionAssert.AreEqual(
            new[] { nameof(QueueItemInfo.IsSwitching), nameof(QueueItemInfo.ContentOpacity) },
            changed);
        Assert.IsTrue(item.ContentOpacity < 1);

        item.IsSwitching = false;

        Assert.AreEqual(1, item.ContentOpacity);
    }

    [TestMethod]
    public void Synchronize_SwitchingItemUnchanged_KeepsSwitchingState()
    {
        var originalItem = CreateItem("1", "Song");
        originalItem.IsSwitching = true;
        var target = new ObservableCollection<QueueItemInfo> { originalItem };

        QueueCollectionSynchronizer.Synchronize(target, [CreateItem("1", "Song")]);

        Assert.IsTrue(target[0].IsSwitching);
    }

    private static QueueItemInfo CreateItem(string id, string name) =>
        new(0, id, name, "Artist", "Album", 180_000, "https://example.test/cover.jpg");
}
