// la versione definitiva (TcpClient.cs) unifica il meglio delle tre 
// - sintassi moderna e nullable da Fael, 
// - robustezza e pending read da Mb, 
// - naming e monitoring da SiDel/Mb 
// e corregge quattro bug presenti in tutte le implementazioni originali: 
// la race condition in Reconnect() (CTS sovrascritto senza cancellare il precedente, risolto con Cancel/Dispose + Interlocked.CompareExchange), 
// _bufferLength statico condiviso tra istanze, MonitorErrors non riavviato dopo riconnessione, 
// e il CancellationTokenSource non disposto nel path di successo di ConnectAsync.

// Versione definitiva del TcpClient che unisce le migliori feature di Fael, SiDel e Mb.
//
// Da Fael:  sintassi C# moderna, nullable annotations corrette, naming conventions consistenti,
//           guard !Connected in ReadAsync, InvalidOperationException dedicato in WriteAsync
// Da Mb:    _pendingReadTask, Disconnect robusto con try/catch, Interlocked per contatore istanze,
//           MonitorErrors con IsConnected(), OnConnected dopo reconnect, structured logging Serilog
// Da SiDel: Name property, Use(ILogger), evento Error, ErrorsPerSecond monitoring
// Fix:      race condition in Reconnect (cancella il vecchio CTS prima di crearne uno nuovo)
//
// .NET 10:  Lock type, ConnectAsync/WriteAsync con CancellationToken nativo,
//           Task.WaitAsync(TimeSpan) per timeout in ReadAsync, file-scoped namespace,
//           using dichiarativi per CancellationTokenSource
// 

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
/// TCP client with automatic reconnection, configurable timeouts and error monitoring.
/// Supports asynchronous read and write with robust disconnection handling.
/// </summary>
public class DefTcpClient
{
    private int _bufferLength = 2048;
    private static int _instanceCounter = 0;
    private const int READ_TIMEOUT = 1000;
    private const int WRITE_TIMEOUT = 3000;

    private readonly Lock _lock = new();
    private CancellationTokenSource? _cancelReconnection;
    private IPAddress? _ipAddress;
    private ILogger? _logger;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    // Traccia il loop MonitorErrors per evitare duplicati
    private Task? _monitorTask;

    // StreamReader non supporta letture concorrenti: manteniamo il task pendente
    // e lo ri-attendiamo con il nuovo timeout invece di lanciarne uno nuovo.
    private Task<int>? _pendingReadTask;
    private char[]? _pendingBuffer;

    /// <summary>
    /// Creates a new instance with an auto-generated name (e.g. <c>TcpClient_0</c>).
    /// </summary>
    public DefTcpClient() => Name = $"{nameof(DefTcpClient)}_{Interlocked.Increment(ref _instanceCounter) - 1}";

    /// <summary>
    /// Creates a new instance with the specified name.
    /// </summary>
    /// <param name="name">Client identifier used in log messages.</param>
    public DefTcpClient(string name) : this() => Name = name;

    public delegate void ConnectedChangedHandler(object sender);

    /// <summary>Raised when a connection attempt fails.</summary>
    public event ConnectedChangedHandler? ConnectionFail;
    /// <summary>Raised when the connection is lost.</summary>
    public event ConnectedChangedHandler? Disconnected;
    /// <summary>Raised when the connection is established or re-established.</summary>
    public event ConnectedChangedHandler? OnConnected;
    /// <summary>Raised when a reconnection attempt begins.</summary>
    public event ConnectedChangedHandler? Reconnecting;
    /// <summary>Raised when a generic error occurs.</summary>
    public event ErrorEventHandler? Error;

    /// <summary>Indicates whether the client is connected.</summary>
    public bool Connected { get; private set; }
    /// <summary>Connection timeout in milliseconds.</summary>
    public int ConnectionTimeout { get; private set; }
    /// <summary>StreamEncoding used for stream read and write operations.</summary>
    public Encoding? StreamEncoding { get; private set; }
    /// <summary>Client identifier used in log messages.</summary>
    public string Name { get; private set; }
    /// <summary>Destination TCP port.</summary>
    public int Port { get; private set; }
    /// <summary>Automatic reconnection policy.</summary>
    public IReconnectionPolicy ReconnectionPolicy { get; set; } = Utils.ReconnectionPolicy.Default;

    /// <summary>Number of errors detected in the last second.</summary>
    public int ErrorsPerSecond { get; private set; }
    /// <summary>Maximum errors per second threshold before considering the connection degraded.</summary>
    public static int MaxErrorsPerSecond = 10;
    private void MonitorErrors()
    {
        lock (_lock)
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
    }

    private async Task<bool> _ReconnectAsync()
    {
        Reconnecting?.Invoke(this);
        var result = await ConnectAsync(_ipAddress!, Port, ConnectionTimeout, StreamEncoding);
        return result.IsConnected;
    }

    protected void OnDisconnection()
    {
        Disconnect();
        Disconnected?.Invoke(this);
        Reconnect();
    }

    /// <summary>Cancels the ongoing reconnection attempt, if any.</summary>
    public void CancelReconnection() => _cancelReconnection?.Cancel();

    /// <summary>
    /// Opens a TCP connection to the specified address and port.
    /// </summary>
    /// <param name="ipAddress">Destination IP address as a string.</param>
    /// <param name="port">Destination TCP port.</param>
    /// <param name="timeout">Connection timeout in milliseconds (default 10000).</param>
    /// <param name="streamEncoding">Stream encoding; if <c>null</c>, UTF-8 is used.</param>
    /// <returns>A <see cref="ConnectResult"/> indicating the connection outcome.</returns>
    public async Task<ConnectResult> ConnectAsync(string ipAddress, int port, int timeout = 10000, Encoding? streamEncoding = null)
    {
        if (ipAddress == "") return ConnectResult.NotConnected();
        if (!IPAddress.TryParse(ipAddress, out var ip)) return ConnectResult.NotConnected();

        return await ConnectAsync(ip, port, timeout, streamEncoding ?? Encoding.UTF8);
    }

    /// <summary>
    /// Opens a TCP connection to the specified address and port.
    /// </summary>
    /// <param name="ipAddress">Destination IP address.</param>
    /// <param name="port">Destination TCP port.</param>
    /// <param name="timeout">Connection timeout in milliseconds (default 10000).</param>
    /// <param name="streamEncoding">Stream encoding; if <c>null</c>, UTF-8 is used.</param>
    /// <returns>A <see cref="ConnectResult"/> indicating the connection outcome.</returns>
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
        MonitorErrors();
        _logger?.Information("{Name} Connected to {IpAddress}:{Port}", Name, _ipAddress, Port);
        OnConnected?.Invoke(this);
        return ConnectResult.Success();
    }

    /// <summary>
    /// Closes the connection and releases all associated resources (stream, reader, writer).
    /// </summary>
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
            _pendingReadTask = null;
            _pendingBuffer = null;
        }
    }

    /// <summary>
    /// Checks whether the TCP connection is active (thread-safe).
    /// </summary>
    /// <returns><c>true</c> if the client is connected and the underlying socket is open.</returns>
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
        catch (IOException e)
        {
            _pendingReadTask = null;
            ErrorsPerSecond++;
            if (ErrorsPerSecond > MaxErrorsPerSecond) Error?.Invoke(this, new ErrorEventArgs(e));
            OnDisconnection();
            return ReadResult.NotConnected();
        }
        catch (ObjectDisposedException e) when (!Connected)
        {
            _pendingReadTask = null;
            ErrorsPerSecond++;
            if (ErrorsPerSecond > MaxErrorsPerSecond) Error?.Invoke(this, new ErrorEventArgs(e));
            _logger?.Debug("{Name} ReadAsync threw: {Message} - disconnected by user?", Name, e.Message);
            return ReadResult.Fail(e);
        }
        catch (InvalidOperationException e)
        {
            _pendingReadTask = null;
            ErrorsPerSecond++;
            if (ErrorsPerSecond > MaxErrorsPerSecond) Error?.Invoke(this, new ErrorEventArgs(e));
            _logger?.Debug("{Name} ReadAsync threw: InvalidOperationException {Message}", Name, e.Message);
            return ReadResult.Fail(e);
        }
        catch (Exception e)
        {
            _pendingReadTask = null;
            ErrorsPerSecond++;
            if (ErrorsPerSecond > MaxErrorsPerSecond) Error?.Invoke(this, new ErrorEventArgs(e));
            var message = $"{e.Message}\n{e.StackTrace}";
            if (e.InnerException != null)
                message = $"{message}\ninner exception:{e.InnerException.Message}\n{e.InnerException.StackTrace}";
            _logger?.Debug("{Name} ReadAsync threw: {Message}", Name, message);
            return ReadResult.Fail(e);
        }
    }

    /// <summary>
    /// Starts an asynchronous reconnection attempt in the background, according to the configured <see cref="ReconnectionPolicy"/>.
    /// Cancels any previous reconnection still in progress.
    /// </summary>
    public void Reconnect()
    {
        _logger?.Debug("{Name} Reconnect({ShouldReconnect})", Name, ReconnectionPolicy.ShouldReconnect);
        if (!ReconnectionPolicy.ShouldReconnect) return;

        var reconnectAgent = new ReconnectAgent();
        var cts = new CancellationTokenSource();

        // Fix race condition: sotto lock, cancella il vecchio CTS e installa il nuovo.
        lock (_lock)
        {
            try { _cancelReconnection?.Cancel(); } catch (ObjectDisposedException) { }
            try { _cancelReconnection?.Dispose(); } catch (ObjectDisposedException) { }
            _cancelReconnection = cts;
        }
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
        });
    }

    /// <summary>
    /// Configures the logger for this instance. The logger is used for structured log messages at various points in the connection lifecycle and error handling.
    /// </summary>
    /// <param name="logger">An <see cref="ILogger"/> instance to be used for logging. If <c>null</c>, logging is disabled.</param>
    public void Use(ILogger logger) => _logger = logger;

    /// <summary>
    /// Writes a command to the TCP stream asynchronously.
    /// </summary>
    /// <param name="command"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
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
        catch (InvalidOperationException e)
        {
            ErrorsPerSecond++;
            if (ErrorsPerSecond > MaxErrorsPerSecond) Error?.Invoke(this, new ErrorEventArgs(e));
            _logger?.Debug("{Name} WriteAsync threw InvalidOperationException, triggering disconnection", Name);
            OnDisconnection();
            return WriteResult.NotConnected();
        }
        catch (IOException e)
        {
            ErrorsPerSecond++;
            if (ErrorsPerSecond > MaxErrorsPerSecond) Error?.Invoke(this, new ErrorEventArgs(e));
            OnDisconnection();
            return WriteResult.NotConnected();
        }
        catch (Exception e)
        {
            ErrorsPerSecond++;
            if (ErrorsPerSecond > MaxErrorsPerSecond) Error?.Invoke(this, new ErrorEventArgs(e));
            var message = $"{e.Message}\n{e.StackTrace}";
            if (e.InnerException != null)
                message = $"{message}\ninner exception:{e.InnerException.Message}\n{e.InnerException.StackTrace}";
            _logger?.Debug("{Name} WriteAsync threw: {Message}", Name, message);
            return WriteResult.Fail(e);
        }
    }
}
