using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TcpClientEvolution.Tests.Helpers;

/// <summary>
/// Helper che avvia un TcpListener su localhost con porta random.
/// Usato nei test per simulare un server TCP senza dipendenze esterne.
/// </summary>
public class TcpListenerHelper : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly List<TcpClient> _acceptedClients = new();

    public int Port { get; }
    public IPAddress Address => IPAddress.Loopback;
    public string AddressString => Address.ToString();

    public TcpListenerHelper()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public async Task<TcpClient> AcceptClientAsync(CancellationToken ct = default)
    {
        var client = await _listener.AcceptTcpClientAsync(ct);
        _acceptedClients.Add(client);
        return client;
    }

    public async Task SendAsync(TcpClient client, string data)
    {
        var writer = new StreamWriter(client.GetStream(), Encoding.UTF8) { AutoFlush = true };
        await writer.WriteAsync(data);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        foreach (var c in _acceptedClients)
            c.Dispose();
    }
}
