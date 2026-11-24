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
