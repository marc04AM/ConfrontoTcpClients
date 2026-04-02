using Sistec.Core;
using Sistec.Core.Utils;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test su MonitorErrors e ErrorsPerSecond.
/// </summary>
public class D07_MonitorErrorsTests : IAsyncLifetime
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
    public void ErrorsPerSecond_Default_Zero()
    {
        var client = new DefTcpClient();
        Assert.Equal(0, client.ErrorsPerSecond);
    }

    [Fact]
    public async Task MonitorErrors_AvviatoDaConnectAsync()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var acceptTask = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await acceptTask;

        // MonitorErrors viene avviato da ConnectAsync (overload string)
        var monitorTask = ReflectionHelper.GetField<Task>(client, "_monitorTask");
        Assert.NotNull(monitorTask);

        client.Disconnect();
    }

    [Fact]
    public async Task MonitorErrors_NonDuplica_SeGiaAttivo()
    {
        var client = new DefTcpClient();
        client.ReconnectionPolicy = new ExponentialBackoffReconnectionPolicy { ShouldReconnect = false };

        var accept1 = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept1;

        var monitorTask1 = ReflectionHelper.GetField<Task>(client, "_monitorTask");

        // Disconnetti e riconnetti: un nuovo MonitorErrors parte solo se il vecchio è completato
        client.Disconnect();
        await Task.Delay(100); // lascia terminare il loop di monitoraggio

        var accept2 = _listener.AcceptClientAsync();
        await client.ConnectAsync(_listener.AddressString, _listener.Port);
        await accept2;

        var monitorTask2 = ReflectionHelper.GetField<Task>(client, "_monitorTask");
        Assert.NotNull(monitorTask2);

        client.Disconnect();
    }

    [Fact]
    public void MaxErrorsPerSecond_Default_10()
    {
        Assert.Equal(10, DefTcpClient.MaxErrorsPerSecond);
    }

    [Fact]
    public async Task BufferLength_Instance_NonStatico()
    {
        // Verifica che _bufferLength sia un campo di istanza, non statico
        var field = typeof(DefTcpClient).GetField("_bufferLength",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.False(field!.IsStatic, "_bufferLength deve essere di istanza, non statico");
    }
}
