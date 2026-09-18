using System.Net;
using System.Net.Sockets;
using Lyrider.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lyrider.Tests;

[TestClass]
public sealed class CiderServiceTests
{
    [TestMethod]
    public async Task GetPlaybackStatusAsync_WhenCiderDoesNotRespond_ReturnsNull()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var service = new CiderService($"http://127.0.0.1:{port}/");

        var status = await service.GetPlaybackStatusAsync(null);

        Assert.IsNull(status);
    }
}
