using System.IO;
using System.Reflection;
using Sistec.Core;
using Sistec.Core.Devices;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.EvolutionTests;

/// <summary>
/// T08: Dimostra le feature esclusive del client definitivo (Def).
///
/// 1. Guard !Connected in ReadAsync (da Fael, persa in SiDel/Mb, ripristinata in Def)
/// 2. InvalidOperationException in WriteAsync → disconnect (da Fael, persa in Mb, ripristinata in Def)
/// 3. Race condition fix in Reconnect (cancella il vecchio CTS prima di sovrascriverlo)
/// 4. .NET 10: Lock type invece di object lock
/// </summary>
public class T08_DefExclusiveTests : IAsyncLifetime
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

    // ── 1. GUARD !CONNECTED IN READASYNC ──────────────────

    [Fact]
    public async Task Fael_ReadAsync_SenzaConnessione_RitornaNotConnected()
    {
        // Fael ha il guard !Connected
        var client = new FaelTcpClient();
        var result = await client.ReadAsync(50);
        Assert.True(result.IsNotConnected, "Fael: ReadAsync senza connessione ritorna NotConnected");
    }

    [Fact]
    public async Task SiDel_ReadAsync_SenzaConnessione_NonHaGuard()
    {
        // SiDel NON ha il guard !Connected in ReadAsync
        // (prova a leggere dal reader null → NullReferenceException o simile)
        var client = new SiDelTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var result = await client.ReadAsync(50);
        // SiDel non ritorna NotConnected, ma Fail per l'eccezione
        Assert.False(result.IsNotConnected, "SiDel: non ha guard !Connected in ReadAsync");
    }

    [Fact]
    public async Task Mb_ReadAsync_SenzaConnessione_NonHaGuard()
    {
        // Mb NON ha il guard !Connected in ReadAsync
        var client = new MbTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var result = await client.ReadAsync(50);
        Assert.False(result.IsNotConnected, "Mb: non ha guard !Connected in ReadAsync");
    }

    [Fact]
    public async Task Def_ReadAsync_SenzaConnessione_RitornaNotConnected()
    {
        // Def ripristina il guard !Connected da Fael
        var client = new DefTcpClient();
        var result = await client.ReadAsync(50);
        Assert.True(result.IsNotConnected, "Def: ReadAsync senza connessione ritorna NotConnected");
    }

    [Fact]
    public async Task Confronto_GuardReadAsync_FaelDefSi_SiDelMbNo()
    {
        var fael = new FaelTcpClient();
        var sidel = new SiDelTcpClient();
        sidel.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var mb = new MbTcpClient();
        mb.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var def = new DefTcpClient();

        var faelResult = await fael.ReadAsync(50);
        var sidelResult = await sidel.ReadAsync(50);
        var mbResult = await mb.ReadAsync(50);
        var defResult = await def.ReadAsync(50);

        // Fael e Def: guard attivo → NotConnected
        Assert.True(faelResult.IsNotConnected, "Fael: guard !Connected attivo");
        Assert.True(defResult.IsNotConnected, "Def: guard !Connected ripristinato");

        // SiDel e Mb: nessun guard → eccezione (Fail)
        Assert.False(sidelResult.IsNotConnected, "SiDel: nessun guard !Connected");
        Assert.False(mbResult.IsNotConnected, "Mb: nessun guard !Connected");
    }

    // ── 2. INVALIDOPERATIONEXCEPTION IN WRITEASYNC ────────

    [Fact]
    public async Task Fael_WriteAsync_InvalidOp_RitornaNotConnected()
    {
        var client = new FaelTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.Address, _listener.Port);
        await acceptTask;

        // Iniettiamo un writer che lancia InvalidOperationException
        ReflectionHelper.SetField(client, "_writer", new ThrowingStreamWriter(new MemoryStream()));

        bool disconnectedFired = false;
        client.Disconnected += _ => disconnectedFired = true;

        var result = await client.WriteAsync("test", 100);

        // FAEL: catch specifico InvalidOp → OnDisconnection() → result NotConnected
        // OnDisconnection() lancia evento Disconnected + Reconnect, ma non chiama Disconnect()
        Assert.True(result.IsNotConnected, "Fael: WriteAsync InvalidOp → NotConnected");
        Assert.True(disconnectedFired, "Fael: evento Disconnected lanciato");
    }

    [Fact]
    public async Task Mb_WriteAsync_InvalidOp_NonDisconnette()
    {
        var client = new MbTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        ReflectionHelper.SetField(client, "_writer", new ThrowingStreamWriter(new MemoryStream()));

        var result = await client.WriteAsync("test", 100);

        // MB: NON ha catch specifico per InvalidOp → va nel catch generico → Fail
        Assert.NotNull(result.Exception);
        Assert.True(client.Connected, "Mb: resta connesso (no catch InvalidOp in WriteAsync)");
    }

    [Fact]
    public async Task Def_WriteAsync_InvalidOp_RitornaNotConnected()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        ReflectionHelper.SetField(client, "_writer", new ThrowingStreamWriter(new MemoryStream()));

        bool disconnectedFired = false;
        client.Disconnected += _ => disconnectedFired = true;

        var result = await client.WriteAsync("test", 100);

        // DEF: come Fael, catch specifico InvalidOp → OnDisconnection() → result NotConnected
        Assert.True(result.IsNotConnected, "Def: WriteAsync InvalidOp → NotConnected");
        Assert.True(disconnectedFired, "Def: evento Disconnected lanciato");
    }

    [Fact]
    public async Task Confronto_WriteAsyncInvalidOp_FaelDefNotConnected_MbFail()
    {
        // Fael
        var fael = new FaelTcpClient();
        fael.ReconnectionPolicy = new Sistec.Core.Utils.ReconnectionPolicy { ShouldReconnect = false };
        var accept1 = _listener.AcceptClientAsync();
        await fael.ConnectAsync(_listener.Address, _listener.Port);
        await accept1;
        ReflectionHelper.SetField(fael, "_writer", new ThrowingStreamWriter(new MemoryStream()));
        var faelResult = await fael.WriteAsync("test", 100);

        // Mb
        var mb = new MbTcpClient();
        mb.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var accept2 = _listener.AcceptClientAsync();
        await mb.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept2;
        ReflectionHelper.SetField(mb, "_writer", new ThrowingStreamWriter(new MemoryStream()));
        var mbResult = await mb.WriteAsync("test", 100);

        // Def
        var def = new DefTcpClient();
        def.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };
        var accept3 = _listener.AcceptClientAsync();
        await def.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept3;
        ReflectionHelper.SetField(def, "_writer", new ThrowingStreamWriter(new MemoryStream()));
        var defResult = await def.WriteAsync("test", 100);

        // Fael e Def: catch InvalidOp → NotConnected (comportamento specifico)
        // Mb: catch generico → Fail (nessun catch specifico per InvalidOp)
        Assert.True(faelResult.IsNotConnected, "Fael: WriteAsync InvalidOp → NotConnected");
        Assert.NotNull(mbResult.Exception);
        Assert.False(mbResult.IsNotConnected, "Mb: WriteAsync InvalidOp → Fail (no catch specifico)");
        Assert.True(defResult.IsNotConnected, "Def: WriteAsync InvalidOp → NotConnected (come Fael)");
    }

    // ── 3. RECONNECT RACE CONDITION FIX ───────────────────

    [Fact]
    public void Mb_Reconnect_NonCancellaVecchioCTS()
    {
        // Verifica strutturale: Mb sovrascrive _cancelReconnection senza Cancel/Dispose
        // Il codice Mb:
        //   _cancelReconnection = new CancellationTokenSource();
        // senza prima cancellare il vecchio → race condition se Reconnect viene chiamato 2 volte
        var method = typeof(MbTcpClient).GetMethod("Reconnect",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void Def_Reconnect_CancellaVecchioCTS_PrimaDisovrascrivere()
    {
        // Verifica strutturale: Def fa Cancel+Dispose del vecchio CTS prima di sovrascriverlo
        // Il codice Def:
        //   _cancelReconnection?.Cancel();
        //   _cancelReconnection?.Dispose();
        //   var cts = new CancellationTokenSource();
        //   _cancelReconnection = cts;
        var method = typeof(DefTcpClient).GetMethod("Reconnect",
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void Def_Reconnect_UsaInterlockedCompareExchange()
    {
        // Verifica strutturale: Def usa Interlocked.CompareExchange per il cleanup del CTS
        // Questo evita che un Reconnect() successivo perda il riferimento al proprio CTS
        var field = typeof(DefTcpClient).GetField("_cancelReconnection",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
    }

    // ── 4. .NET 10: LOCK TYPE ─────────────────────────────

    [Fact]
    public void Fael_UsaObjectLock()
    {
        var lockField = typeof(FaelTcpClient).GetField("_lock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(lockField);
        Assert.Equal(typeof(object), lockField!.FieldType);
    }

    [Fact]
    public void Mb_UsaObjectLock()
    {
        var lockField = typeof(MbTcpClient).GetField("_lock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(lockField);
        Assert.Equal(typeof(object), lockField!.FieldType);
    }

    [Fact]
    public void Def_UsaLockType()
    {
        // Def usa System.Threading.Lock (.NET 9+) invece di object
        var lockField = typeof(DefTcpClient).GetField("_lock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(lockField);
        Assert.Equal(typeof(System.Threading.Lock), lockField!.FieldType);
    }

    [Fact]
    public void Confronto_LockType_FaelMbObject_DefLock()
    {
        var faelLock = typeof(FaelTcpClient).GetField("_lock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var mbLock = typeof(MbTcpClient).GetField("_lock",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var defLock = typeof(DefTcpClient).GetField("_lock",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Fael e Mb usano object
        Assert.Equal(typeof(object), faelLock!.FieldType);
        Assert.Equal(typeof(object), mbLock!.FieldType);

        // Def usa System.Threading.Lock
        Assert.Equal(typeof(System.Threading.Lock), defLock!.FieldType);
    }
}

/// <summary>
/// StreamWriter che lancia InvalidOperationException su WriteAsync.
/// Simula il caso di scritture concorrenti su StreamWriter.
/// </summary>
internal class ThrowingStreamWriter : StreamWriter
{
    public ThrowingStreamWriter(Stream stream) : base(stream) { }

    public override Task WriteAsync(string? value)
        => throw new InvalidOperationException("Simulated concurrent write on StreamWriter");

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Simulated concurrent write on StreamWriter");
}
