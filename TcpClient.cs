// la versione definitiva (TcpClient.cs) unifica il meglio delle tre 
// - sintassi moderna e nullable da Fael, 
// - robustezza e pending read da Mb, 
// - naming e monitoring da SiDel/Mb 
// e corregge quattro bug presenti in tutte le implementazioni originali: 
// la race condition in Reconnect() (CTS sovrascritto senza cancellare il precedente, risolto con Cancel/Dispose + Interlocked.CompareExchange), 
// _bufferLength statico condiviso tra istanze, MonitorErrors non riavviato dopo riconnessione, 
// e il CancellationTokenSource non disposto nel path di successo di ConnectAsync.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Sistec.Core.Interfaces;
using Sistec.Core.Utils;

namespace Sistec.Core;

/// <summary>
/// Versione definitiva del TcpClient che unisce le migliori feature di Fael, SiDel e Mb.
///
/// Da Fael:  sintassi C# moderna, nullable annotations corrette, naming conventions consistenti,
///           guard !Connected in ReadAsync, InvalidOperationException dedicato in WriteAsync
/// Da Mb:    _pendingReadTask, Disconnect robusto con try/catch, Interlocked per contatore istanze,
///           MonitorErrors con IsConnected(), OnConnected dopo reconnect, structured logging Serilog
/// Da SiDel: Name property, Use(ILogger), evento Error, ErrorsPerSecond monitoring
/// Fix:      race condition in Reconnect (cancella il vecchio CTS prima di crearne uno nuovo)
///
/// .NET 10:  Lock type, ConnectAsync/WriteAsync con CancellationToken nativo,
///           Task.WaitAsync(TimeSpan) per timeout in ReadAsync, file-scoped namespace,
///           using dichiarativi per CancellationTokenSource
/// </summary>
public class TcpClient
{
    private int _bufferLength = 2048;
    private static int _instanceCounter = 0;
    private static int READ_TIMEOUT = 1000;
    private static int WRITE_TIMEOUT = 3000;

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cancelReconnection;
    private IPAddress? _ipAddress;
    private ILogger? _logger;
    private System.Net.Sockets.TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    // Traccia il loop MonitorErrors per evitare duplicati
    private Task? _monitorTask;

    // StreamReader non supporta letture concorrenti: manteniamo il task pendente
    // e lo ri-attendiamo con il nuovo timeout invece di lanciarne uno nuovo.
    private Task<int>? _pendingReadTask;
    private char[]? _pendingBuffer;

    public TcpClient() => Name = $"{nameof(TcpClient)}_{Interlocked.Increment(ref _instanceCounter) - 1}";

    public TcpClient(string name) : this() => Name = name;

    public delegate void ConnectedChangedHandler(object sender);

    public event ConnectedChangedHandler? ConnectionFail;
    public event ConnectedChangedHandler? Disconnected;
    public event ConnectedChangedHandler? OnConnected;
    public event ConnectedChangedHandler? Reconnecting;
    public event ErrorEventHandler? Error;

    public bool Connected { get; private set; }
    public int ConnectionTimeout { get; private set; }
    public Encoding? StreamEncoding { get; private set; }
    public string Name { get; private set; }
    public int Port { get; private set; }
    public IReconnectionPolicy ReconnectionPolicy { get; set; } = ExponentialBackoffReconnectionPolicy.Default;

    public int ErrorsPerSecond { get; private set; }
    public static int MaxErrorsPerSecond = 10;

    private void MonitorErrors()
    {
        // Se il loop precedente e' ancora attivo, non ne lanciamo un altro
        if (_monitorTask != null && !_monitorTask.IsCompleted) return;
        _monitorTask = Task.Run(async () =>
        {
            while (IsConnected())
            {
                ErrorsPerSecond = 0;
                await Task.Delay(1000);
            }
            ErrorsPerSecond = 0;
        });
    }

    private async Task<bool> _ReconnectAsync()
    {
        Reconnecting?.Invoke(this);
        var result = await ConnectAsync(_ipAddress!, Port, ConnectionTimeout, StreamEncoding);
        return result.IsConnected;
    }

    protected void OnDisconnection()
    {
        Disconnected?.Invoke(this);
        Reconnect();
    }

    public void CancelReconnection() => _cancelReconnection?.Cancel();

    public async Task<ConnectResult> ConnectAsync(string ipAddress, int port, int timeout = 10000, Encoding? streamEncoding = null)
    {
        if (ipAddress == "") return ConnectResult.NotConnected();
        if (!IPAddress.TryParse(ipAddress, out var ip)) return ConnectResult.NotConnected();

        var result = await ConnectAsync(ip, port, timeout, streamEncoding ?? Encoding.UTF8);
        if (result.IsConnected)
        {
            MonitorErrors();
        }
        return result;
    }

    public async Task<ConnectResult> ConnectAsync(IPAddress ipAddress, int port, int timeout = 10000, Encoding? streamEncoding = null)
    {
        if (IsConnected()) return ConnectResult.AlreadyConnected();
        _ipAddress = ipAddress;
        Port = port;
        ConnectionTimeout = timeout;

        Disconnect();
        _tcpClient = new System.Net.Sockets.TcpClient { NoDelay = true };
        StreamEncoding = streamEncoding ?? Encoding.UTF8;

        _logger?.Information("{Name} Trying to connect to {IpAddress}:{Port}", Name, _ipAddress, Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(ConnectionTimeout));
        try
        {
            await _tcpClient.ConnectAsync(_ipAddress, Port, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.Warning("{Name} Connection timeout", Name);
            ConnectionFail?.Invoke(this);
            return ConnectResult.Timeout();
        }
        catch (IOException e)
        {
            _logger?.Warning("{Name} Connection IOException: {Message}", Name, e.Message);
            Disconnect();
            ConnectionFail?.Invoke(this);
            return ConnectResult.NotConnected();
        }
        catch (Exception e)
        {
            _logger?.Error(e, "{Name} Connection error", Name);
            ConnectionFail?.Invoke(this);
            return ConnectResult.Fail(e);
        }

        if (!_tcpClient.Connected)
        {
            _logger?.Warning("{Name} Connection failed", Name);
            ConnectionFail?.Invoke(this);
            return ConnectResult.NotConnected();
        }
        _stream = _tcpClient.GetStream();
        _bufferLength = _tcpClient.ReceiveBufferSize;

        _reader = new StreamReader(_stream, StreamEncoding!);
        _writer = new StreamWriter(_stream, StreamEncoding!) { AutoFlush = true };
        Connected = true;
        _logger?.Information("{Name} Connected to {IpAddress}:{Port}", Name, _ipAddress, Port);
        OnConnected?.Invoke(this);
        return ConnectResult.Success();
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            Connected = false;
            _stream?.Close();
            try { _reader?.Dispose(); }
            catch (InvalidOperationException e) { _logger?.Debug("{Name} Disconnect: reader dispose skipped — async read in progress ({EMessage})", Name, e.Message); }
            try { _writer?.Dispose(); }
            catch (InvalidOperationException e) { _logger?.Debug("{Name} Disconnect: writer dispose skipped — async write in progress ({EMessage})", Name, e.Message); }
            _tcpClient?.Close();
            _tcpClient = null;
            _stream = null;
            _reader = null;
            _writer = null;
        }
    }

    public bool IsConnected()
    {
        lock (_lock)
        {
            return Connected && (_tcpClient?.Connected ?? false);
        }
    }

    public async Task<ReadResult> ReadAsync(int timeout = -1)
    {
        if (timeout == -1) timeout = READ_TIMEOUT;
        if (!Connected) return ReadResult.NotConnected();
        try
        {
            // Se il task precedente e' completato, consumiamo i suoi dati prima di lanciarne uno nuovo.
            // Senza questo check, un task che completa tra un timeout e la chiamata successiva
            // verrebbe sovrascritto, perdendo i dati ricevuti.
            if (_pendingReadTask != null && _pendingReadTask.IsCompleted)
            {
                var pending = _pendingReadTask;
                _pendingReadTask = null;
                var completedCount = await pending; // non blocca, e' gia' completato
                var completedResponse = new string(_pendingBuffer!, 0, completedCount);
                return string.IsNullOrWhiteSpace(completedResponse)
                    ? ReadResult.NoData()
                    : ReadResult.Success(completedResponse);
            }

            // Se una ReadAsync precedente e' ancora in volo, la ri-attendiamo:
            // StreamReader non supporta letture concorrenti.
            if (_pendingReadTask == null)
            {
                // Cattura locale: _reader puo' essere nullato da Disconnect() concorrente
                var reader = _reader;
                if (reader == null) return ReadResult.NotConnected();
                if (_pendingBuffer == null || _pendingBuffer.Length != _bufferLength)
                    _pendingBuffer = new char[_bufferLength];
                _pendingReadTask = reader.ReadAsync(_pendingBuffer, 0, _pendingBuffer.Length);
            }

            var count = await _pendingReadTask.WaitAsync(TimeSpan.FromMilliseconds(timeout));
            // WaitAsync ha completato: il task originale e' terminato
            var response = new string(_pendingBuffer!, 0, count);
            _pendingReadTask = null;
            return string.IsNullOrWhiteSpace(response)
                ? ReadResult.NoData()
                : ReadResult.Success(response);
        }
        catch (TimeoutException)
        {
            // task ancora in volo, verra' riusato alla prossima chiamata
            return ReadResult.Timeout();
        }
        catch (IOException)
        {
            _pendingReadTask = null;
            OnDisconnection();
            return ReadResult.NotConnected();
        }
        catch (ObjectDisposedException e) when (!Connected)
        {
            _pendingReadTask = null;
            _logger?.Debug("{Name} ReadAsync threw: {Message} - disconnected by user?", Name, e.Message);
            return ReadResult.Fail(e);
        }
        catch (InvalidOperationException e)
        {
            _pendingReadTask = null;
            _logger?.Debug("{Name} ReadAsync threw: InvalidOperationException {Message}", Name, e.Message);
            Disconnect();
            OnDisconnection();
            return ReadResult.NotConnected();
        }
        catch (Exception e)
        {
            _pendingReadTask = null;
            var message = $"{e.Message}\n{e.StackTrace}";
            if (e.InnerException != null)
                message = $"{message}\ninner exception:{e.InnerException.Message}\n{e.InnerException.StackTrace}";
            _logger?.Debug("{Name} ReadAsync threw: {Message}", Name, message);
            return ReadResult.Fail(e);
        }
    }

    public void Reconnect()
    {
        _logger?.Debug("{Name} Reconnect({ShouldReconnect})", Name, ReconnectionPolicy.ShouldReconnect);
        if (!ReconnectionPolicy.ShouldReconnect) return;

        // Fix race condition: cancella il vecchio CTS prima di sovrascriverlo.
        // try/catch: il Task.Run precedente potrebbe aver gia' disposto il CTS.
        try { _cancelReconnection?.Cancel(); } catch (ObjectDisposedException) { }
        try { _cancelReconnection?.Dispose(); } catch (ObjectDisposedException) { }

        var reconnectAgent = new ReconnectAgent();
        var cts = new CancellationTokenSource();
        _cancelReconnection = cts;
        Task.Run(async () =>
        {
            try
            {
                await reconnectAgent.ReconnectAsync(_ReconnectAsync, cts.Token, ReconnectionPolicy);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "{Name} Reconnection failed", Name);
            }
            cts.Dispose();
            // Annulla il riferimento solo se e' ancora il nostro CTS
            // (un nuovo Reconnect() potrebbe averlo gia' sostituito)
            Interlocked.CompareExchange(ref _cancelReconnection, null, cts);
            _logger?.Debug("{Name} Reconnection COMPLETE {Connected}, {IsConnected}", Name, Connected, IsConnected());
            if (IsConnected())
            {
                MonitorErrors();
                OnConnected?.Invoke(this);
            }
        });
    }

    public void Use(ILogger logger) => _logger = logger;

    public async Task<WriteResult> WriteAsync(string command, int timeout = -1)
    {
        if (timeout == -1) timeout = WRITE_TIMEOUT;
        if (!Connected) return WriteResult.NotConnected();
        // Cattura locale: _writer puo' essere nullato da Disconnect() concorrente
        var writer = _writer;
        if (writer == null) return WriteResult.NotConnected();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
        try
        {
            await writer.WriteAsync(command.AsMemory(), cts.Token);
            return WriteResult.Success();
        }
        catch (OperationCanceledException)
        {
            return WriteResult.Timeout();
        }
        catch (InvalidOperationException)
        {
            _logger?.Debug("{Name} WriteAsync threw InvalidOperationException, triggering disconnection", Name);
            OnDisconnection();
            return WriteResult.NotConnected();
        }
        catch (IOException)
        {
            OnDisconnection();
            return WriteResult.NotConnected();
        }
        catch (Exception e)
        {
            var message = $"{e.Message}\n{e.StackTrace}";
            if (e.InnerException != null)
                message = $"{message}\ninner exception:{e.InnerException.Message}\n{e.InnerException.StackTrace}";
            _logger?.Debug("{Name} WriteAsync threw: {Message}", Name, message);
            return WriteResult.Fail(e);
        }
    }
}
