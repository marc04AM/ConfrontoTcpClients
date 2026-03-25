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

namespace Sistec.Core.Devices
{
    public class TcpClient
    {
        private readonly object _lock = new object();
        private CancellationTokenSource _cancelReconnection;
        private IPAddress _ipAddress;
        private ILogger _logger;
        private StreamReader _reader;
        private NetworkStream _stream;
        private System.Net.Sockets.TcpClient _tcpc;
        private StreamWriter _writer;

        private static int bufferLength = 2048;
        private static int i = 0;
        public static int READ_TIMEOUT = 1000;
        public static int WRITE_TIMEOUT = 3000;

        public TcpClient() => Name = $"{nameof(TcpClient)}_{i++}";

        public TcpClient(string name) : this() { }// => Name = name;

        public delegate void ConnectedChangedHandler(object sender);

        public event ConnectedChangedHandler ConnectionFail;
        public event ConnectedChangedHandler Disconnected;
        public event ConnectedChangedHandler OnConnected;
        public event ConnectedChangedHandler Reconnecting;
        public event ErrorEventHandler Error;

        public bool Connected { get; private set; }
        public int ConnectionTimeout { get; private set; }
        public Encoding Encoding { get; private set; }
        public string Name { get; private set; }
        public int Port { get; private set; }
        public IReconnectionPolicy ReconnectionPolicy { get; set; } = ExponentialBackoffReconnectionPolicy.Default;

        public int ErrorsPerSecond { get; private set; }
        public static int MaxErrorsPerSecond = 10;

        private void MonitorErrors()
        {
            Task.Run(async () =>
            {
                while (Connected)
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

        public async Task<ConnectResult> ConnectAsync(string ipAddress, int port, int timeout = 10000, Encoding encoding = null)
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

        public async Task<ConnectResult> ConnectAsync(IPAddress ipAddress, int port, int timeout = 10000, Encoding encoding = null)
        {
            if (IsConnected()) return ConnectResult.AlreadyConnected();
            _ipAddress = ipAddress;
            Port = port;
            ConnectionTimeout = timeout;

            Disconnect();
            _tcpc = new System.Net.Sockets.TcpClient { NoDelay = true };
            Encoding = encoding ?? Encoding.UTF8;

            _logger?.Information($"{Name} Trying to connect to {_ipAddress}:{Port}");
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
                        _logger?.Warning($"{Name} Connection timeout");
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
                _logger?.Error(e, $"{Name} Connection error");
                ConnectionFail?.Invoke(this);
                return ConnectResult.Fail(e);
            }

            if (!_tcpc.Connected)
            {
                _logger?.Warning($"{Name} Connection failed");
                ConnectionFail?.Invoke(this);
                return ConnectResult.NotConnected();
            }
            _stream = _tcpc.GetStream();
            bufferLength = _tcpc.ReceiveBufferSize;

            _reader = new StreamReader(_stream, Encoding);
            _writer = new StreamWriter(_stream, Encoding) { AutoFlush = true };
            Connected = true;
            _logger?.Information($"{Name} Connected to {_ipAddress}:{Port}");

            return ConnectResult.Success();
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                Connected = false;
                _stream?.Close();
                _reader?.Dispose();
                _writer?.Dispose();
                _tcpc?.Close();
                _tcpc = null;
                _stream = null;
                _reader = null;
                _writer = null;
            }
        }

        public bool IsConnected()
        {
            lock (_lock)
            {
                return Connected && (_tcpc?.Connected ?? false);
            }
        }

        public async Task<ReadResult> ReadAsync(int timeout = -1)
        {
            if (timeout == -1) timeout = READ_TIMEOUT;
            var timeOut = TimeSpan.FromMilliseconds(timeout);
            var cancellationCompletionSource = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(timeOut);
            var buffer = new char[bufferLength];
            try
            {
                var readTask = _reader.ReadAsync(buffer, 0, buffer.Length);
                using (cts.Token.Register(() => cancellationCompletionSource.TrySetResult(true)))
                {
                    if (readTask != await Task.WhenAny(readTask, cancellationCompletionSource.Task))
                    {
                        //_logger?.Warning("Connection timeout");
                        return ReadResult.Timeout();
                    }
                }

                var count = await readTask;
                var response = new string(buffer, 0, count);
                return string.IsNullOrWhiteSpace(response)
                    ? ReadResult.NoData()
                    : ReadResult.Success(response);
            }
            catch (IOException e)
            {
                OnDisconnection();
                return ReadResult.NotConnected();
            }
            catch (ObjectDisposedException e) when (!Connected)
            {
                var message = $"{e.Message} - disconnected by user?";
                _logger?.Debug($"{Name} ReadAsync threw: {message}");
                return ReadResult.Fail(e);
            }
            catch (InvalidOperationException e) 
            {
                ErrorsPerSecond++;
                var message = $"InvalidOperationException {e.Message}";
                _logger?.Debug($"{Name} ReadAsync threw: {message}");
                if (ErrorsPerSecond > MaxErrorsPerSecond)
                {
                    Error?.Invoke(this, new ErrorEventArgs(e));
                }

                return ReadResult.Fail(e);
            }
            catch (Exception e)
            {
                var message = $"{e.Message}\n{e.StackTrace}";
                if (e.InnerException != null)
                {
                    message = $"{message}\ninner exception:{e.InnerException.Message}\n{e.InnerException.StackTrace}";
                }

                _logger?.Debug($"{Name} ReadAsync threw: {message}");
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
                Utilities.Logger?.Debug($"{Name} Reconnection COMPLETE {Connected}, {IsConnected()}");
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
                //var writeTask = _writer.WriteAsync(command);
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
                _logger?.Debug($"{Name} WriteAsync threw: {message}");
                return WriteResult.Fail(e);
            }
            finally
            {
                cts.Dispose();
            }
        }
    }
}