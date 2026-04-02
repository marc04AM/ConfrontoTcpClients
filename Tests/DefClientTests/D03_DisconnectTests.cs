using Sistec.Core;
using Sistec.Core.Utils;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test su Disconnect e IsConnected.
/// </summary>
public class D03_DisconnectTests : IAsyncLifetime
{
    private TcpListenerHelper _listener = null!;

    public async Task InitializeAsync()
    {
        _listener = new TcpListenerHelper();
    }

    public async Task DisposeAsync()
    {
        await _listener.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_ImpostaConnectedFalse()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        Assert.True(client.Connected);

        client.Disconnect();

        Assert.False(client.Connected);
    }

    [Fact]
    public async Task IsConnected_DopoDisconnect_RitornaFalse()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        Assert.True(client.IsConnected());

        client.Disconnect();

        Assert.False(client.IsConnected());
    }

    [Fact]
    public void Disconnect_SenzaConnessione_NonLanciaEccezioni()
    {
        var client = new DefTcpClient();

        var ex = Record.Exception(() => client.Disconnect());
        Assert.Null(ex);
    }

    [Fact]
    public void Disconnect_Multiplo_NonLanciaEccezioni()
    {
        var client = new DefTcpClient();

        var ex = Record.Exception(() =>
        {
            client.Disconnect();
            client.Disconnect();
            client.Disconnect();
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task Disconnect_NullificaCampiInterni()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        client.Disconnect();

        Assert.Null(ReflectionHelper.GetField<System.Net.Sockets.TcpClient>(client, "_tcpClient"));
        Assert.Null(ReflectionHelper.GetField<System.IO.StreamReader>(client, "_reader"));
        Assert.Null(ReflectionHelper.GetField<System.IO.StreamWriter>(client, "_writer"));
        Assert.Null(ReflectionHelper.GetField<System.Net.Sockets.NetworkStream>(client, "_stream"));
    }

    [Fact]
    public void IsConnected_SenzaConnessione_RitornaFalse()
    {
        var client = new DefTcpClient();
        Assert.False(client.IsConnected());
    }
}
