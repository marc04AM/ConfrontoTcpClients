using System.Reflection;
using Sistec.Core;
using Sistec.Core.Utils;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test sugli eventi del DefTcpClient: OnConnected, Disconnected, ConnectionFail, Reconnecting, Error.
/// </summary>
public class D05_EventsTests : IAsyncLifetime
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
    public void DefTcpClient_HaTuttiGliEventi()
    {
        Assert.True(ReflectionHelper.HasEvent(typeof(DefTcpClient), "OnConnected"));
        Assert.True(ReflectionHelper.HasEvent(typeof(DefTcpClient), "Disconnected"));
        Assert.True(ReflectionHelper.HasEvent(typeof(DefTcpClient), "ConnectionFail"));
        Assert.True(ReflectionHelper.HasEvent(typeof(DefTcpClient), "Reconnecting"));
        Assert.True(ReflectionHelper.HasEvent(typeof(DefTcpClient), "Error"));
    }

    [Fact]
    public async Task OnDisconnection_LanciaDisconnectedEReconnecting()
    {
        var client = new DefTcpClient();
        // Usiamo ReconnectionPolicy con delay breve per non aspettare troppo
        client.ReconnectionPolicy = new FastReconnectionPolicy();

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        bool disconnectedFired = false;
        bool reconnectingFired = false;
        client.Disconnected += _ => disconnectedFired = true;
        client.Reconnecting += _ => reconnectingFired = true;

        // Forza IOException su WriteAsync per triggerare OnDisconnection
        ReflectionHelper.SetField(client, "_writer", new IOExceptionWriter(new MemoryStream()));
        await client.WriteAsync("test", 100);

        // Aspetta che il ReconnectAgent faccia il delay e chiami _ReconnectAsync
        await Task.Delay(500);

        Assert.True(disconnectedFired, "Disconnected deve essere lanciato");
        Assert.True(reconnectingFired, "Reconnecting deve essere lanciato quando ShouldReconnect=true");

        client.CancelReconnection();
    }

    [Fact]
    public async Task CancelReconnection_InterrompeReconnect()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = true };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        // Triggera disconnessione → Reconnect parte
        ReflectionHelper.SetField(client, "_writer", new IOExceptionWriter(new MemoryStream()));
        await client.WriteAsync("test", 100);

        // CancelReconnection deve funzionare senza eccezioni
        var ex = Record.Exception(() => client.CancelReconnection());
        Assert.Null(ex);
    }

    [Fact]
    public async Task ErrorEvent_LanciatoDopMaxErrorsPerSecond()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        bool errorFired = false;
        client.Error += (_, _) => errorFired = true;

        // Inietta un reader che lancia InvalidOperationException
        ReflectionHelper.SetField(client, "_reader", new ThrowingReader(new MemoryStream()));

        // Ogni ReadAsync con InvalidOp incrementa ErrorsPerSecond.
        // Dopo MaxErrorsPerSecond+1 chiamate, l'evento Error viene lanciato.
        for (int i = 0; i <= DefTcpClient.MaxErrorsPerSecond; i++)
        {
            await client.ReadAsync(100);
            // Re-inietta il reader perché _pendingReadTask viene nullato dopo l'eccezione
            ReflectionHelper.SetField(client, "_reader", new ThrowingReader(new MemoryStream()));
        }

        Assert.True(errorFired, "Error deve essere lanciato quando ErrorsPerSecond > MaxErrorsPerSecond");

        client.Disconnect();
    }
}

internal class ThrowingReader : StreamReader
{
    public ThrowingReader(Stream stream) : base(stream) { }

    public override Task<int> ReadAsync(char[] buffer, int index, int count)
        => throw new InvalidOperationException("Simulated concurrent read");
}

internal class FastReconnectionPolicy : Sistec.Core.Interfaces.IReconnectionPolicy
{
    public bool ShouldReconnect { get; set; } = true;
    public TimeSpan GetNextDelay(int attempt) => TimeSpan.FromMilliseconds(50);
}
