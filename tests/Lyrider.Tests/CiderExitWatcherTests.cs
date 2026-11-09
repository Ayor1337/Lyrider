using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class CiderExitWatcherTests
{
    [TestMethod]
    public void Poll_CiderNeverSeen_DoesNotExit()
    {
        var watcher = new CiderExitWatcher(() => false);

        Assert.IsFalse(watcher.Poll());
        Assert.IsFalse(watcher.Poll());
    }

    [TestMethod]
    public void Poll_CiderRunning_DoesNotExit()
    {
        var watcher = new CiderExitWatcher(() => true);

        Assert.IsFalse(watcher.Poll());
        Assert.IsFalse(watcher.Poll());
    }

    [TestMethod]
    public void Poll_CiderStartsAfterLyriderThenQuits_ExitsOnce()
    {
        var running = false;
        var watcher = new CiderExitWatcher(() => running);

        Assert.IsFalse(watcher.Poll());
        running = true;
        Assert.IsFalse(watcher.Poll());
        running = false;
        Assert.IsTrue(watcher.Poll());
        Assert.IsFalse(watcher.Poll());
    }

    [TestMethod]
    public void Poll_ResetAfterCiderSeen_WaitsForCiderAgain()
    {
        var running = true;
        var watcher = new CiderExitWatcher(() => running);
        watcher.Poll();

        watcher.Reset();
        running = false;

        Assert.IsFalse(watcher.Poll());
    }
}
