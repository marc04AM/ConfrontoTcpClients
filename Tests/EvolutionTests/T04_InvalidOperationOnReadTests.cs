using System.IO;
using System.Net;
using Sistec.Core.Devices;
using Sistec.Core;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.EvolutionTests;

/// <summary>
/// T04: Dimostra la gestione di InvalidOperationException in ReadAsync.
/// - Fael: disconnect aggressivo → chiama Disconnect() + OnDisconnection(), ritorna NotConnected
/// - SiDel: soft error → incrementa ErrorsPerSecond, ritorna Fail, lancia Error sopra soglia
/// - Mb: soft error → stessa gestione di SiDel + reset _pendingReadTask
/// - Def: soft error → come Mb (incrementa ErrorsPerSecond, Fail, reset _pendingReadTask)
/// </summary>
public class T04_InvalidOperationOnReadTests : IAsyncLifetime
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

    // ── FAEL: DISCONNECT AGGRESSIVO ──────────────────────

    [Fact]
    public async Task Fael_ReadAsync_InvalidOp_DisconnetteCeRitornaNotConnected()
    {
        var client = new FaelTcpClient();
        // Impostiamo ShouldReconnect = false per evitare loop
        client.ReconnectionPolicy = new Sistec.Core.Utils.ReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        var connectResult = await client.ConnectAsync(_listener.Address, _listener.Port);
        await acceptTask;
        Assert.True(connectResult.IsConnected);
        Assert.True(client.Connected);

        // Iniettiamo un reader che lancia InvalidOperationException
        var fakeStream = new MemoryStream();
        var throwingReader = new ThrowingStreamReader(fakeStream);
        ReflectionHelper.SetField(client, "_reader", throwingReader);

        var result = await client.ReadAsync(100);

        // FAEL: disconnette aggressivamente
        Assert.True(result.IsNotConnected, "Fael deve ritornare NotConnected su InvalidOperationException");
        Assert.False(client.Connected, "Fael deve disconnettere su InvalidOperationException");
    }

    // ── SIDEL: SOFT ERROR ────────────────────────────────

    [Fact]
    public async Task SiDel_ReadAsync_InvalidOp_IncrementaErrorsERitornaFail()
    {
        var client = new SiDelTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        var connectResult = await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;
        Assert.True(connectResult.IsConnected);

        // Iniettiamo un reader che lancia InvalidOperationException
        var fakeStream = new MemoryStream();
        var throwingReader = new ThrowingStreamReader(fakeStream);
        ReflectionHelper.SetField(client, "_reader", throwingReader);

        int initialErrors = client.ErrorsPerSecond;
        var result = await client.ReadAsync(100);

        // SIDEL: soft error, non disconnette
        Assert.NotNull(result.Exception);
        Assert.True(client.Connected, "SiDel NON deve disconnettere su InvalidOperationException");
        Assert.True(client.ErrorsPerSecond > initialErrors, "SiDel deve incrementare ErrorsPerSecond");
    }

    [Fact]
    public async Task SiDel_ReadAsync_InvalidOp_LanciaErrorEventSopraSoglia()
    {
        var client = new SiDelTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        var connectResult = await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;
        Assert.True(connectResult.IsConnected);

        var fakeStream = new MemoryStream();
        var throwingReader = new ThrowingStreamReader(fakeStream);
        ReflectionHelper.SetField(client, "_reader", throwingReader);

        bool errorEventFired = false;
        client.Error += (sender, args) => errorEventFired = true;

        // Generiamo MaxErrorsPerSecond + 1 errori per superare la soglia
        for (int j = 0; j <= SiDelTcpClient.MaxErrorsPerSecond; j++)
        {
            await client.ReadAsync(100);
        }

        Assert.True(errorEventFired, "SiDel deve lanciare l'evento Error quando ErrorsPerSecond > MaxErrorsPerSecond");
    }

    // ── MB: SOFT ERROR (COME SIDEL) ──────────────────────

    [Fact]
    public async Task Mb_ReadAsync_InvalidOp_IncrementaErrorsERitornaFail()
    {
        var client = new MbTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        var connectResult = await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;
        Assert.True(connectResult.IsConnected);

        var fakeStream = new MemoryStream();
        var throwingReader = new ThrowingStreamReader(fakeStream);
        ReflectionHelper.SetField(client, "_reader", throwingReader);

        int initialErrors = client.ErrorsPerSecond;
        var result = await client.ReadAsync(100);

        // MB: soft error come SiDel
        Assert.NotNull(result.Exception);
        Assert.True(client.Connected, "Mb NON deve disconnettere su InvalidOperationException");
        Assert.True(client.ErrorsPerSecond > initialErrors, "Mb deve incrementare ErrorsPerSecond");
    }

    // ── DEF: SOFT ERROR (COME MB) ───────────────────────

    [Fact]
    public async Task Def_ReadAsync_InvalidOp_IncrementaErrorsERitornaFail()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        var connectResult = await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;
        Assert.True(connectResult.IsConnected);

        var fakeStream = new MemoryStream();
        var throwingReader = new ThrowingStreamReader(fakeStream);
        ReflectionHelper.SetField(client, "_reader", throwingReader);

        int initialErrors = client.ErrorsPerSecond;
        var result = await client.ReadAsync(100);

        // DEF: soft error come Mb
        Assert.NotNull(result.Exception);
        Assert.True(client.Connected, "Def NON deve disconnettere su InvalidOperationException");
        Assert.True(client.ErrorsPerSecond > initialErrors, "Def deve incrementare ErrorsPerSecond");
    }

    // ── CONFRONTO DIRETTO ────────────────────────────────

    [Fact]
    public async Task Confronto_InvalidOp_FaelDisconnette_SiDelMbDefContano()
    {
        // Fael
        var fael = new FaelTcpClient();
        fael.ReconnectionPolicy = new Sistec.Core.Utils.ReconnectionPolicy { ShouldReconnect = false };
        var accept1 = _listener.AcceptClientAsync();
        await fael.ConnectAsync(_listener.Address, _listener.Port);
        await accept1;
        ReflectionHelper.SetField(fael, "_reader", new ThrowingStreamReader(new MemoryStream()));
        await fael.ReadAsync(100);
        bool faelConnectedAfter = fael.Connected;

        // SiDel
        var sidel = new SiDelTcpClient();
        sidel.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var accept2 = _listener.AcceptClientAsync();
        await sidel.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept2;
        ReflectionHelper.SetField(sidel, "_reader", new ThrowingStreamReader(new MemoryStream()));
        await sidel.ReadAsync(100);
        bool sidelConnectedAfter = sidel.Connected;

        // Mb
        var mb = new MbTcpClient();
        mb.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var accept3 = _listener.AcceptClientAsync();
        await mb.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept3;
        ReflectionHelper.SetField(mb, "_reader", new ThrowingStreamReader(new MemoryStream()));
        await mb.ReadAsync(100);
        bool mbConnectedAfter = mb.Connected;

        // Def
        var def = new DefTcpClient();
        def.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var accept4 = _listener.AcceptClientAsync();
        await def.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept4;
        ReflectionHelper.SetField(def, "_reader", new ThrowingStreamReader(new MemoryStream()));
        await def.ReadAsync(100);
        bool defConnectedAfter = def.Connected;

        // Fael disconnette, SiDel, Mb e Def restano connessi
        Assert.False(faelConnectedAfter, "Fael: disconnette su InvalidOp");
        Assert.True(sidelConnectedAfter, "SiDel: resta connesso su InvalidOp");
        Assert.True(mbConnectedAfter, "Mb: resta connesso su InvalidOp");
        Assert.True(defConnectedAfter, "Def: resta connesso su InvalidOp");
    }
}

/// <summary>
/// StreamReader che lancia InvalidOperationException su ReadAsync.
/// Simula il caso di letture concorrenti su StreamReader.
/// </summary>
internal class ThrowingStreamReader : StreamReader
{
    public ThrowingStreamReader(Stream stream) : base(stream) { }

    public override Task<int> ReadAsync(char[] buffer, int index, int count)
        => throw new InvalidOperationException("Simulated concurrent read on StreamReader");
}
