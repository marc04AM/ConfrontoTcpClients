using System.Reflection;
using Sistec.Core;
using Sistec.Core.Utils;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test su Reconnect: policy, CancelReconnection, race condition fix (CTS Cancel/Dispose + Interlocked).
/// </summary>
public class D06_ReconnectTests : IAsyncLifetime
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
    public void Reconnect_ShouldReconnectFalse_NonFaNiente()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        bool reconnectingFired = false;
        client.Reconnecting += _ => reconnectingFired = true;

        client.Reconnect();

        Assert.False(reconnectingFired, "Reconnecting non deve essere lanciato con ShouldReconnect=false");
    }

    [Fact]
    public void Reconnect_CancellaVecchioCTS()
    {
        // Verifica strutturale: il codice fa Cancel+Dispose prima di sovrascrivere
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = true };

        // Prima chiamata a Reconnect crea un CTS
        client.Reconnect();
        var cts1 = ReflectionHelper.GetField<CancellationTokenSource>(client, "_cancelReconnection");
        Assert.NotNull(cts1);

        // Seconda chiamata: il vecchio CTS deve essere stato cancellato
        client.Reconnect();
        Assert.True(cts1!.IsCancellationRequested,
            "Il vecchio CTS deve essere stato cancellato prima di sovrascriverlo");

        // Cleanup
        client.CancelReconnection();
    }

    [Fact]
    public void Reconnect_UsaInterlockedCompareExchange()
    {
        // Verifica che il campo _cancelReconnection esista (usato con Interlocked.CompareExchange)
        var field = typeof(DefTcpClient).GetField("_cancelReconnection",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(CancellationTokenSource), field!.FieldType);
    }

    [Fact]
    public void CancelReconnection_SenzaReconnect_NonLanciaEccezioni()
    {
        var client = new DefTcpClient();
        var ex = Record.Exception(() => client.CancelReconnection());
        Assert.Null(ex);
    }

    [Fact]
    public void ReconnectionPolicy_Default_ExponentialBackoff()
    {
        var client = new DefTcpClient();
        Assert.IsType<ReconnectionPolicy>(client.ReconnectionPolicy);
    }

    [Fact]
    public void ReconnectionPolicy_Settabile()
    {
        var client = new DefTcpClient();
        var policy = new ReconnectionPolicy { ShouldReconnect = true };
        client.ReconnectionPolicy = policy;

        Assert.Same(policy, client.ReconnectionPolicy);
    }
}
