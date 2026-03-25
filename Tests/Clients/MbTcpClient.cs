using System;
using Serilog;
using Sistec.Core.Interfaces;
using Sistec.Core.Utils;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sistec.Core
{
    /// <summary>
    /// Versione piu' matura (Mb): thread-safe counter, costruttore corretto,
    /// pending read reuse, disconnect sicuro, logging strutturato Serilog.
    /// </summary>
    public class MbTcpClient
    {
        private readonly object _lock = new object();
        private CancellationTokenSource? _cancelReconnection;
        private IPAddress _ipAddress = null!;
        private ILogger? _logger;
        private StreamReader _reader = null!;
        private NetworkStream _stream = null!;
        private System.Net.Sockets.TcpClient? _tcpc;
        private StreamWriter _writer = null!;

        private static int bufferLength = 2048;
        private static int _instanceCounter = 0;
        private static int READ_TIMEOUT = 1000;
        private static int WRITE_TIMEOUT = 3000;

        // MB: campi per il riuso del task di lettura pendente
        private Task<int>? _pendingReadTask;
        private char[]?    _pendingBuffer;

        // MB: contatore thread-safe con Interlocked.Increment
        public MbTcpClient() => Name = $"TcpClient_{Interlocked.Increment(ref _instanceCounter) - 1}";

        // MB: costruttore con nome FUNZIONANTE (fix del bug SiDel)
        public MbTcpClient(string name) : this() { Name = name; }

        public delegate void ConnectedChangedHandler(object sender);

        public event ConnectedChangedHandler? ConnectionFail;
        public event ConnectedChangedHandler? Disconnected;
        public event ConnectedChangedHandler? OnConnected;
        public event ConnectedChangedHandler? Reconnecting;
        public event ErrorEventHandler? Error;

        public bool Connected { get; private set; }
        public int ConnectionTimeout { get; private set; }
        public Encoding? Encoding { get; private set; }
        public string Name { get; private set; }
        public int Port { get; private set; }
        public IReconnectionPolicy ReconnectionPolicy { get; set; } = ExponentialBackoffReconnectionPolicy.Default;

        public int ErrorsPerSecond { get; private set; }
        public static int MaxErrorsPerSecond = 10;

        // MB: usa IsConnected() (lock-protected) invece di Connected
        private void MonitorErrors()
        {
            Task.Run(async () =>
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
            var result = await ConnectAsync(_ipAddress, Port, ConnectionTimeout, Encoding);
            return result.IsConnected;
        }

        protected void OnDisconnection()
        {
            Debug.Print($"{Name} OnDisconnection {Connected}, {IsConnected()}");
            Disconnected?.Invoke(this);
            Reconnect();
        }

        public void CancelReconnection() => _cancelReconnection?.Cancel();

        public async Task<ConnectResult> ConnectAsync(string ipAddress, int port, int timeout = 10000, Encoding? encoding = null)
        {
            if (ipAddress == "") return ConnectResult.NotConnected();
            if (!IPAddress.TryParse(ipAddress, out var ip)) return ConnectResult.NotConnected();

            var result = await ConnectAsync(ip, port, timeout, encoding);
            if (result.IsConnected)
            {
                MonitorErrors();
                OnConnected?.Invoke(this);
            }
            return result;
        }

        public async Task<ConnectResult> ConnectAsync(IPAddress ipAddress, int port, int timeout = 10000, Encoding? encoding = null)
        {
            if (IsConnected()) return ConnectResult.AlreadyConnected();
            _ipAddress = ipAddress;
            Port = port;
            ConnectionTimeout = timeout;

            Disconnect();
            _tcpc = new System.Net.Sockets.TcpClient { NoDelay = true };
            Encoding = encoding ?? Encoding.UTF8;

            // MB: logging strutturato con template Serilog
            _logger?.Information("{Name} Trying to connect to {IpAddress}:{Port}", Name, _ipAddress, Port);
            var timeOut = TimeSpan.FromMilliseconds(ConnectionTimeout);
            var cancellationCompletionSource = new TaskCompletionSource<bool>();
            try
            {
                var cts = new CancellationTokenSource(timeOut);
                var task = _tcpc.ConnectAsync(_ipAddress, Port);
                using (cts.Token.Register(() => cancellationCompletionSource.TrySetResult(true)))
                {
                    if (task != await Task.WhenAny(task, cancellationCompletionSource.Task))
                    {
                        _logger?.Warning("{Name} Connection timeout", Name);
                        ConnectionFail?.Invoke(this);
                        return ConnectResult.Timeout();
                    }
                }
            }
            catch (IOException)
            {
                OnDisconnection();
                ConnectionFail?.Invoke(this);
                return ConnectResult.NotConnected();
            }
            catch (Exception e)
            {
                _logger?.Error(e, "{Name} Connection error", Name);
                ConnectionFail?.Invoke(this);
                return ConnectResult.Fail(e);
            }

            if (!_tcpc.Connected)
            {
                _logger?.Warning("{Name} Connection failed", Name);
                ConnectionFail?.Invoke(this);
                return ConnectResult.NotConnected();
            }
            _stream = _tcpc.GetStream();
            bufferLength = _tcpc.ReceiveBufferSize;

            _reader = new StreamReader(_stream, Encoding);
            _writer = new StreamWriter(_stream, Encoding) { AutoFlush = true };
            Connected = true;
            _logger?.Information("{Name} Connected to {IpAddress}:{Port}", Name, _ipAddress, Port);

            return ConnectResult.Success();
        }

        // MB: disconnect sicuro con try/catch su Dispose
        public void Disconnect()
        {
            lock (_lock)
            {
                Connected = false;
                _stream?.Close();
                try { _reader?.Dispose(); }
                catch (InvalidOperationException e) { _logger?.Debug("{Name} Disconnect: reader dispose skipped ({EMessage})", Name, e.Message); }
                try { _writer?.Dispose(); }
                catch (InvalidOperationException e) { _logger?.Debug("{Name} Disconnect: writer dispose skipped ({EMessage})", Name, e.Message); }
                _tcpc?.Close();
                _tcpc = null;
                _stream = null!;
                _reader = null!;
                _writer = null!;
            }
        }

        public bool IsConnected()
        {
            lock (_lock)
            {
                return Connected && (_tcpc?.Connected ?? false);
            }
        }

        // MB: pending read task reuse
        public async Task<ReadResult> ReadAsync(int timeout = -1)
        {
            if (timeout == -1) timeout = READ_TIMEOUT;
            var timeoutSource = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
            try
            {
                // Se un ReadAsync precedente e' ancora in volo, lo ri-attende
                if (_pendingReadTask == null || _pendingReadTask.IsCompleted)
                {
                    if (_pendingBuffer == null || _pendingBuffer.Length != bufferLength)
                        _pendingBuffer = new char[bufferLength];
                    _pendingReadTask = _reader.ReadAsync(_pendingBuffer, 0, _pendingBuffer.Length);
                }

                using (cts.Token.Register(() => timeoutSource.TrySetResult(true)))
                {
                    if (_pendingReadTask != await Task.WhenAny(_pendingReadTask, timeoutSource.Task))
                        return ReadResult.Timeout(); // task ancora in volo, sara' riusato al prossimo giro
                }

                var count    = await _pendingReadTask;
                var response = new string(_pendingBuffer, 0, count);
                _pendingReadTask = null;
                return string.IsNullOrWhiteSpace(response)
                    ? ReadResult.NoData()
                    : ReadResult.Success(response);
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
                var message = $"{e.Message} - disconnected by user?";
                _logger?.Debug("{Name} ReadAsync threw: {Message}", Name, message);
                return ReadResult.Fail(e);
            }
            catch (InvalidOperationException e)
            {
                _pendingReadTask = null;
                ErrorsPerSecond++;
                var message = $"InvalidOperationException {e.Message}";
                _logger?.Debug("{Name} ReadAsync threw: {Message}", Name, message);
                if (ErrorsPerSecond > MaxErrorsPerSecond)
                    Error?.Invoke(this, new ErrorEventArgs(e));
                return ReadResult.Fail(e);
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
            finally
            {
                cts?.Dispose();
            }
        }

        public void Reconnect()
        {
            if (!ReconnectionPolicy.ShouldReconnect) return;
            var reconnectAgent = new ReconnectAgent();
            _cancelReconnection = new CancellationTokenSource();
            Task.Run(async () =>
            {
                try
                {
                    await reconnectAgent.ReconnectAsync(_ReconnectAsync, _cancelReconnection.Token, ReconnectionPolicy);
                }
                catch (Exception e)
                {
                    _logger?.Error(e, $"Reconnection failed");
                }
                _cancelReconnection?.Dispose();
                _cancelReconnection = null;
                Utilities.Logger?.Debug("{Name} Reconnection COMPLETE {Connected}, {IsConnected}", Name, Connected, IsConnected());
                if (IsConnected())
                {
                    OnConnected?.Invoke(this);
                }
            });
        }

        public void Use(ILogger logger) => _logger = logger;

        public async Task<WriteResult> WriteAsync(string command, int timeout = -1)
        {
            if (timeout == -1) timeout = WRITE_TIMEOUT;
            var timeOut = TimeSpan.FromMilliseconds(timeout);
            var cancellationCompletionSource = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(timeOut);
            try
            {
                var writeTask = _writer.WriteAsync(command);
                using (cts.Token.Register(() => cancellationCompletionSource.TrySetResult(true)))
                {
                    if (writeTask != await Task.WhenAny(writeTask, cancellationCompletionSource.Task))
                    {
                        return WriteResult.Timeout();
                    }
                }
                await writeTask;
                return WriteResult.Success();
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
                {
                    message = $"{message}\ninner exception:{e.InnerException.Message}\n{e.InnerException.StackTrace}";
                }
                _logger?.Debug("{Name} WriteAsync threw: {Message}", Name, message);
                return WriteResult.Fail(e);
            }
            finally
            {
                cts.Dispose();
            }
        }
    }
}
