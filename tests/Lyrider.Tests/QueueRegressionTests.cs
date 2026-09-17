using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
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

    [TestMethod]
    public void TryFindArtworkUrl_ArtworkNestedUnderAttributes_ReturnsUrl()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "1",
              "attributes": {
                "name": "Song",
                "artwork": {
                  "url": "https://example.test/{w}x{h}.jpg"
                }
              }
            }
            """);

        var url = CiderService.TryFindArtworkUrl(document.RootElement);

        Assert.AreEqual("https://example.test/{w}x{h}.jpg", url);
    }

    private static QueueItemInfo CreateItem(string id, string name) =>
        new(0, id, name, "Artist", "Album", 180_000, "https://example.test/cover.jpg");
}
