using System.Net;
using System.Text;
using Sistec.Core;
using Sistec.Core.Utils;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test su ConnectAsync: connessione riuscita, timeout, IP invalido, doppia connessione.
/// </summary>
public class D02_ConnectTests : IAsyncLifetime
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
    public async Task ConnectAsync_ConStringaIP_Successo()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        var result = await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        Assert.True(result.IsConnected);
        Assert.True(client.Connected);
        client.Disconnect();
    }

    [Fact]
    public async Task ConnectAsync_ConIPAddress_Successo()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        var result = await client.ConnectAsync(_listener.Address, _listener.Port);
        await acceptTask;

        Assert.True(result.IsConnected);
        Assert.True(client.Connected);
        client.Disconnect();
    }

    [Fact]
    public async Task ConnectAsync_StringaVuota_RitornaNotConnected()
    {
        var client = new DefTcpClient();
        var result = await client.ConnectAsync("", 1234);

        Assert.False(result.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_IPInvalido_RitornaNotConnected()
    {
        var client = new DefTcpClient();
        var result = await client.ConnectAsync("not-an-ip", 1234);

        Assert.False(result.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_PortaSbagliata_Timeout()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        // Porta 1 non avrà nessun listener → timeout o connection refused
        var result = await client.ConnectAsync("127.0.0.1", 1, timeout: 200);

        Assert.False(result.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_GiaConnesso_RitornaAlreadyConnected()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var accept1 = _listener.AcceptClientAsync();
        var result1 = await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept1;

        Assert.True(result1.IsConnected);

        // Seconda connessione allo stesso server
        var result2 = await client.ConnectAsync(_listener.Address, _listener.Port);
        Assert.True(result2.IsConnected); // AlreadyConnected è comunque IsConnected=true

        client.Disconnect();
    }

    [Fact]
    public async Task ConnectAsync_ImpostaProprietaCorrettamente()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port, timeout: 5000, streamEncoding: Encoding.ASCII);
        await acceptTask;

        Assert.Equal(_listener.Port, client.Port);
        Assert.Equal(5000, client.ConnectionTimeout);
        Assert.Equal(Encoding.ASCII, client.StreamEncoding);

        client.Disconnect();
    }

    [Fact]
    public async Task ConnectAsync_EncodingDefault_UTF8()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        Assert.Equal(Encoding.UTF8, client.StreamEncoding);

        client.Disconnect();
    }

    [Fact]
    public async Task ConnectAsync_Successo_LanciaEventoOnConnected()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        bool eventFired = false;
        client.OnConnected += _ => eventFired = true;

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        Assert.True(eventFired, "OnConnected deve essere lanciato dopo connessione riuscita");

        client.Disconnect();
    }

    [Fact]
    public async Task ConnectAsync_Fallimento_LanciaEventoConnectionFail()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        bool eventFired = false;
        client.ConnectionFail += _ => eventFired = true;

        await client.ConnectAsync("127.0.0.1", 1, timeout: 200);

        Assert.True(eventFired, "ConnectionFail deve essere lanciato dopo connessione fallita");
    }
}
