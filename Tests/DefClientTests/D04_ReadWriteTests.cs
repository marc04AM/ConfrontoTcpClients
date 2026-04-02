using System.IO;
using System.Net.Sockets;
using Sistec.Core;
using Sistec.Core.Utils;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test su ReadAsync e WriteAsync: guard, successo, timeout, disconnessione.
/// </summary>
public class D04_ReadWriteTests : IAsyncLifetime
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

    // ── READ ──────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_SenzaConnessione_RitornaNotConnected()
    {
        var client = new DefTcpClient();
        var result = await client.ReadAsync(50);

        Assert.True(result.IsNotConnected);
    }

    [Fact]
    public async Task ReadAsync_ConDati_RitornaSuccess()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        var serverClient = await acceptTask;

        await _listener.SendAsync(serverClient, "hello");

        var result = await client.ReadAsync(2000);

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Data);

        client.Disconnect();
    }

    [Fact]
    public async Task ReadAsync_SenzaDati_RitornaTimeout()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        var result = await client.ReadAsync(100);

        Assert.True(result.IsTimeout);

        client.Disconnect();
    }

    [Fact]
    public async Task ReadAsync_PendingRead_RiusaTaskPrecedente()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        var serverClient = await acceptTask;

        // Prima lettura senza dati → timeout, ma _pendingReadTask resta in volo
        var result1 = await client.ReadAsync(100);
        Assert.True(result1.IsTimeout);

        // Verifica che _pendingReadTask non è null (il task è ancora in volo)
        var pendingTask = ReflectionHelper.GetField<Task<int>>(client, "_pendingReadTask");
        Assert.NotNull(pendingTask);

        // Ora inviamo dati: la seconda lettura dovrebbe consumarli
        await _listener.SendAsync(serverClient, "delayed-data");

        var result2 = await client.ReadAsync(2000);
        Assert.True(result2.IsSuccess);
        Assert.Equal("delayed-data", result2.Data);

        client.Disconnect();
    }

    [Fact]
    public async Task ReadAsync_DopoDisconnect_RitornaNotConnected()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        client.Disconnect();

        var result = await client.ReadAsync(50);
        Assert.True(result.IsNotConnected);
    }

    // ── WRITE ─────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_SenzaConnessione_RitornaNotConnected()
    {
        var client = new DefTcpClient();
        var result = await client.WriteAsync("test");

        Assert.True(result.IsNotConnected);
    }

    [Fact]
    public async Task WriteAsync_Connesso_RitornaSuccess()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        var result = await client.WriteAsync("hello");

        Assert.True(result.IsSuccess);

        client.Disconnect();
    }

    [Fact]
    public async Task WriteAsync_DopoDisconnect_RitornaNotConnected()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        client.Disconnect();

        var result = await client.WriteAsync("test");
        Assert.True(result.IsNotConnected);
    }

    [Fact]
    public async Task WriteAsync_InvalidOp_RitornaNotConnectedEDisconnette()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        // Inietta writer che lancia InvalidOperationException
        ReflectionHelper.SetField(client, "_writer", new ThrowingWriter(new MemoryStream()));

        bool disconnectedFired = false;
        client.Disconnected += _ => disconnectedFired = true;

        var result = await client.WriteAsync("test", 100);

        Assert.True(result.IsNotConnected);
        Assert.True(disconnectedFired);
    }

    [Fact]
    public async Task WriteAsync_IOException_RitornaNotConnectedEDisconnette()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        ReflectionHelper.SetField(client, "_writer", new IOExceptionWriter(new MemoryStream()));

        bool disconnectedFired = false;
        client.Disconnected += _ => disconnectedFired = true;

        var result = await client.WriteAsync("test", 100);

        Assert.True(result.IsNotConnected);
        Assert.True(disconnectedFired);
    }
}

internal class ThrowingWriter : StreamWriter
{
    public ThrowingWriter(Stream stream) : base(stream) { }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulated concurrent write");
}

internal class IOExceptionWriter : StreamWriter
{
    public IOExceptionWriter(Stream stream) : base(stream) { }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        => throw new IOException("Simulated IO error");
}
