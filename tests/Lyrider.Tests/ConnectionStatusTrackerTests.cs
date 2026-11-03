using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class ConnectionStatusTrackerTests
{
    [TestMethod]
    public void Report_TransientOfflineAfterConnected_KeepsConnectedUntilThreshold()
    {
        var tracker = new ConnectionStatusTracker();
        tracker.Report(CiderConnectionState.Connected, "connected");

        for (var i = 1; i < ConnectionStatusTracker.FailureThreshold; i++)
        {
            Assert.IsFalse(tracker.Report(CiderConnectionState.Offline, "offline"));
            Assert.AreEqual(CiderConnectionState.Connected, tracker.State);
        }

        Assert.IsTrue(tracker.Report(CiderConnectionState.Offline, "offline"));
        Assert.AreEqual(CiderConnectionState.Offline, tracker.State);
        Assert.AreEqual("offline", tracker.Message);
    }

    [TestMethod]
    public void Report_FailureBeforeFirstConnection_StaysPendingUntilThreshold()
    {
        var tracker = new ConnectionStatusTracker();

        Assert.IsFalse(tracker.Report(CiderConnectionState.Error, "error"));
        Assert.IsNull(tracker.State);
        Assert.IsFalse(tracker.Report(CiderConnectionState.Error, "error"));
        Assert.IsTrue(tracker.Report(CiderConnectionState.Error, "error"));
        Assert.AreEqual(CiderConnectionState.Error, tracker.State);
    }

    [TestMethod]
    public void Report_Unauthorized_AppliesImmediately()
    {
        var tracker = new ConnectionStatusTracker();
        tracker.Report(CiderConnectionState.Connected, "connected");

        Assert.IsTrue(tracker.Report(CiderConnectionState.Unauthorized, "token"));
        Assert.AreEqual(CiderConnectionState.Unauthorized, tracker.State);
    }

    [TestMethod]
    public void Report_FailureWhileAlreadyFailed_UpdatesImmediately()
    {
        var tracker = new ConnectionStatusTracker();
        tracker.Report(CiderConnectionState.Unauthorized, "token");

        Assert.IsTrue(tracker.Report(CiderConnectionState.Offline, "offline"));
        Assert.AreEqual(CiderConnectionState.Offline, tracker.State);
    }

    [TestMethod]
    public void Report_ConnectedBetweenFailures_RestartsGraceWindow()
    {
        var tracker = new ConnectionStatusTracker();
        tracker.Report(CiderConnectionState.Connected, "connected");
        tracker.Report(CiderConnectionState.Offline, "offline");
        tracker.Report(CiderConnectionState.Offline, "offline");
        tracker.Report(CiderConnectionState.Connected, "connected");

        Assert.IsFalse(tracker.Report(CiderConnectionState.Offline, "offline"));
        Assert.AreEqual(CiderConnectionState.Connected, tracker.State);
    }

    [TestMethod]
    public void Reset_AfterFailure_ReturnsToPendingState()
    {
        var tracker = new ConnectionStatusTracker();
        tracker.Report(CiderConnectionState.Unauthorized, "token");

        tracker.Reset();

        Assert.IsNull(tracker.State);
        Assert.AreEqual(string.Empty, tracker.Message);
    }

    [TestMethod]
    [DataRow(null, "ConnectingBrush", (int)ConnectionAction.None, false)]
    [DataRow(CiderConnectionState.Connected, "ConnectedBrush", (int)ConnectionAction.None, false)]
    [DataRow(CiderConnectionState.Unauthorized, "WarningBrush", (int)ConnectionAction.Configure, true)]
    [DataRow(CiderConnectionState.Offline, "DisconnectedBrush", (int)ConnectionAction.Retry, true)]
    [DataRow(CiderConnectionState.Error, "DisconnectedBrush", (int)ConnectionAction.Retry, true)]
    public void For_State_ReturnsTieredPresentation(
        CiderConnectionState? state,
        string brushKey,
        int action,
        bool hasIssue)
    {
        var display = ConnectionStatusPresentation.For(state);

        Assert.AreEqual(brushKey, display.BrushKey);
        Assert.AreEqual((ConnectionAction)action, display.Action);
        Assert.AreEqual(hasIssue, display.IssueTitle is not null);
    }
}
