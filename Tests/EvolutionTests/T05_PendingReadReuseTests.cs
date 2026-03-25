using Sistec.Core.Devices;
using Sistec.Core;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.EvolutionTests;

/// <summary>
/// T05: Dimostra il meccanismo di pending read reuse presente in Mb e Def.
/// - Fael/SiDel: nessun campo _pendingReadTask, ogni ReadAsync crea un nuovo task
/// - Mb: ha _pendingReadTask e _pendingBuffer, riusa il task in volo dopo timeout
/// - Def: come Mb, con Task.WaitAsync(TimeSpan) invece di TaskCompletionSource+WhenAny
///
/// Problema risolto: StreamReader non supporta letture concorrenti. Se ReadAsync
/// va in timeout e viene richiamata, Fael/SiDel creano una seconda lettura concorrente
/// causando InvalidOperationException. Mb e Def riusano il task pendente.
/// </summary>
public class T05_PendingReadReuseTests : IAsyncLifetime
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

    // ── VERIFICA STRUTTURALE ─────────────────────────────

    [Fact]
    public void Fael_NonHaCampiPendingRead()
    {
        Assert.False(ReflectionHelper.HasField(typeof(FaelTcpClient), "_pendingReadTask"),
            "FaelTcpClient non ha _pendingReadTask");
        Assert.False(ReflectionHelper.HasField(typeof(FaelTcpClient), "_pendingBuffer"),
            "FaelTcpClient non ha _pendingBuffer");
    }

    [Fact]
    public void SiDel_NonHaCampiPendingRead()
    {
        Assert.False(ReflectionHelper.HasField(typeof(SiDelTcpClient), "_pendingReadTask"),
            "SiDelTcpClient non ha _pendingReadTask");
        Assert.False(ReflectionHelper.HasField(typeof(SiDelTcpClient), "_pendingBuffer"),
            "SiDelTcpClient non ha _pendingBuffer");
    }

    [Fact]
    public void Mb_HaCampiPendingRead()
    {
        Assert.True(ReflectionHelper.HasField(typeof(MbTcpClient), "_pendingReadTask"),
            "MbTcpClient deve avere _pendingReadTask");
        Assert.True(ReflectionHelper.HasField(typeof(MbTcpClient), "_pendingBuffer"),
            "MbTcpClient deve avere _pendingBuffer");
    }

    // ── COMPORTAMENTO: TIMEOUT E RIUSO ───────────────────

    [Fact]
    public async Task Mb_ReadAsync_DopoTimeout_RiusaTaskPendente()
    {
        var client = new MbTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        var serverClient = await acceptTask;

        // Prima lettura con timeout breve (il server non invia nulla)
        var result1 = await client.ReadAsync(50);
        Assert.True(result1.IsTimeout, "Prima lettura deve andare in timeout");

        // Verifica che _pendingReadTask è non-null (task ancora in volo)
        var pendingTask = ReflectionHelper.GetField<Task<int>>(client, "_pendingReadTask");
        Assert.NotNull(pendingTask);
        Assert.False(pendingTask!.IsCompleted, "Il task pendente non deve essere completato");

        // Il server ora invia dati
        await _listener.SendAsync(serverClient, "HELLO");

        // Seconda lettura: Mb riusa il task pendente e riceve i dati
        var result2 = await client.ReadAsync(2000);
        Assert.True(result2.IsSuccess, "Seconda lettura deve avere successo riusando il task pendente");
        Assert.Contains("HELLO", result2.Data!);

        // Dopo la lettura completata, _pendingReadTask viene azzerato
        var pendingAfter = ReflectionHelper.GetField<Task<int>>(client, "_pendingReadTask");
        Assert.Null(pendingAfter);
    }

    [Fact]
    public async Task Mb_ReadAsync_PendingBuffer_VieneRiusato()
    {
        var client = new MbTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        // Prima lettura → crea il buffer
        await client.ReadAsync(50);

        var buffer1 = ReflectionHelper.GetField<char[]>(client, "_pendingBuffer");
        Assert.NotNull(buffer1);

        // Seconda lettura → riusa lo stesso buffer
        await client.ReadAsync(50);

        var buffer2 = ReflectionHelper.GetField<char[]>(client, "_pendingBuffer");
        Assert.Same(buffer1, buffer2); // Stesso riferimento = riuso
    }

    // ── DEF ────────────────────────────────────────────────

    [Fact]
    public void Def_HaCampiPendingRead()
    {
        Assert.True(ReflectionHelper.HasField(typeof(DefTcpClient), "_pendingReadTask"),
            "DefTcpClient deve avere _pendingReadTask");
        Assert.True(ReflectionHelper.HasField(typeof(DefTcpClient), "_pendingBuffer"),
            "DefTcpClient deve avere _pendingBuffer");
    }

    [Fact]
    public async Task Def_ReadAsync_DopoTimeout_RiusaTaskPendente()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        var serverClient = await acceptTask;

        // Prima lettura con timeout breve (il server non invia nulla)
        var result1 = await client.ReadAsync(50);
        Assert.True(result1.IsTimeout, "Prima lettura deve andare in timeout");

        // Verifica che _pendingReadTask è non-null (task ancora in volo)
        var pendingTask = ReflectionHelper.GetField<Task<int>>(client, "_pendingReadTask");
        Assert.NotNull(pendingTask);
        Assert.False(pendingTask!.IsCompleted, "Il task pendente non deve essere completato");

        // Il server ora invia dati
        await _listener.SendAsync(serverClient, "HELLO");

        // Seconda lettura: Def riusa il task pendente e riceve i dati
        var result2 = await client.ReadAsync(2000);
        Assert.True(result2.IsSuccess, "Seconda lettura deve avere successo riusando il task pendente");
        Assert.Contains("HELLO", result2.Data!);

        // Dopo la lettura completata, _pendingReadTask viene azzerato
        var pendingAfter = ReflectionHelper.GetField<Task<int>>(client, "_pendingReadTask");
        Assert.Null(pendingAfter);
    }

    [Fact]
    public async Task Def_ReadAsync_PendingBuffer_VieneRiusato()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new Sistec.Core.Utils.ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        // Prima lettura → crea il buffer
        await client.ReadAsync(50);

        var buffer1 = ReflectionHelper.GetField<char[]>(client, "_pendingBuffer");
        Assert.NotNull(buffer1);

        // Seconda lettura → riusa lo stesso buffer
        await client.ReadAsync(50);

        var buffer2 = ReflectionHelper.GetField<char[]>(client, "_pendingBuffer");
        Assert.Same(buffer1, buffer2); // Stesso riferimento = riuso
    }

    // ── CONFRONTO ────────────────────────────────────────

    [Fact]
    public void Confronto_PendingRead_MbEDefLoHanno()
    {
        // Fael e SiDel non hanno il meccanismo di pending read
        Assert.False(ReflectionHelper.HasField(typeof(FaelTcpClient), "_pendingReadTask"));
        Assert.False(ReflectionHelper.HasField(typeof(SiDelTcpClient), "_pendingReadTask"));

        // Mb e Def lo hanno
        Assert.True(ReflectionHelper.HasField(typeof(MbTcpClient), "_pendingReadTask"));
        Assert.True(ReflectionHelper.HasField(typeof(DefTcpClient), "_pendingReadTask"));
    }
}
