using System;
using Sistec.Core.Interfaces;
using Sistec.Core.Utils;
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
        private static int _bufferLength = 2048;
        private readonly object _lock = new();
        private CancellationTokenSource? _cancelReconnection;
        private IPAddress? _ipAddress;
        private System.Net.Sockets.TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        public static int READ_TIMEOUT = 1000;
        public static int WRITE_TIMEOUT = 3000;

        public delegate void ConnectedChangedHandler(object sender);

        public event ConnectedChangedHandler? ConnectionFail;
        public event ConnectedChangedHandler? Disconnected;
        public event ConnectedChangedHandler? OnConnected;
        public event ConnectedChangedHandler? Reconnecting;

        public bool Connected { get; private set; }
        public int ConnectionTimeout { get; private set; }
        public Encoding? Encoding { get; private set; }
        public int Port { get; private set; }
        public IReconnectionPolicy ReconnectionPolicy { get; set; } = Sistec.Core.Utils.ReconnectionPolicy.Default;

        private async Task<bool> _ReconnectAsync()
        {
            Reconnecting?.Invoke(this);
            var result = await ConnectAsync(_ipAddress!, Port, ConnectionTimeout, Encoding);
            return result.IsConnected;
        }

        protected void OnDisconnection()
        {
            //Debug.Print($"{nameof(TcpClient)} OnDisconnection {Connected}, {IsConnected()}");
            Disconnected?.Invoke(this);
            Reconnect();
        }

        public void CancelReconnection() => _cancelReconnection?.Cancel();

        public async Task<ConnectResult> ConnectAsync(string ipAddress, int port, int timeout = 10000, Encoding? encoding = null)
        {
            if (ipAddress == "") return ConnectResult.NotConnected();
            if (!IPAddress.TryParse(ipAddress, out var ip)) return ConnectResult.NotConnected();
            return await ConnectAsync(ip, port, timeout, encoding);
        }

        public async Task<ConnectResult> ConnectAsync(IPAddress ipAddress, int port, int timeout = 10000, Encoding? encoding = null)
        {
            if (IsConnected()) return ConnectResult.AlreadyConnected();
            _ipAddress = ipAddress;
            Port = port;
            ConnectionTimeout = timeout;

            Disconnect();
            _tcpClient = new System.Net.Sockets.TcpClient { NoDelay = true };
            Encoding = encoding ?? Encoding.UTF8;

            Utilities.Logger?.Information($"Trying to connect to {_ipAddress}:{Port}");
            var timeOut = TimeSpan.FromMilliseconds(ConnectionTimeout);
            var cancellationCompletionSource = new TaskCompletionSource<bool>();
            try
            {
                var cts = new CancellationTokenSource(timeOut);
                var task = _tcpClient.ConnectAsync(_ipAddress, Port);
                await using (cts.Token.Register(() => cancellationCompletionSource.TrySetResult(true)))
                {
                    if (task != await Task.WhenAny(task, cancellationCompletionSource.Task))
                    {
                        Utilities.Logger?.Warning($"{_ipAddress}:{Port} Connection timeout");
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
                Utilities.Logger?.Error(e, $"{_ipAddress}:{Port} Connection error");
                ConnectionFail?.Invoke(this);
                return ConnectResult.Fail(e);
            }

            if (!_tcpClient.Connected)
            {
                Utilities.Logger?.Warning($"{_ipAddress}:{Port} Connection failed");
                ConnectionFail?.Invoke(this);
                return ConnectResult.NotConnected();
            }
            _stream = _tcpClient.GetStream();
            _bufferLength = _tcpClient.ReceiveBufferSize;

            _reader = new StreamReader(_stream, Encoding);
            _writer = new StreamWriter(_stream, Encoding) { AutoFlush = true };
            Connected = true;
            Utilities.Logger?.Information($"Connected to {_ipAddress}:{Port}");
            //Task.Run(async () =>
            //{
            //    await Task.Delay(10);
            OnConnected?.Invoke(this);
            //});
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
            var timeOut = TimeSpan.FromMilliseconds(timeout);
            var taskCompletionSource = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(timeOut);
            var buffer = new char[_bufferLength];
            try
            {
                var readTask = _reader.ReadAsync(buffer, 0, buffer.Length);
                await using (cts.Token.Register(() => taskCompletionSource.TrySetResult(true)))
                {
                    if (readTask != await Task.WhenAny(readTask, taskCompletionSource.Task))
                    {
                        Utilities.Logger?.Warning($"{_ipAddress}:{Port} Connection timeout");
                        return ReadResult.Timeout();
                    }
                }
                var count = await readTask;
                var response = new string(buffer, 0, count);
                return string.IsNullOrWhiteSpace(response)
                    ? ReadResult.NoData()
                    : ReadResult.Success(response);
            }
            catch (InvalidOperationException)
            {
                Utilities.Logger?.Debug($"{_ipAddress}:{Port} ReadAsync threw {nameof(InvalidOperationException)}, socket {(_tcpClient == null ? "NULL" : _tcpClient.Connected ? "Connected": "NOT Connected")}");
                Disconnect();
                OnDisconnection();
                return ReadResult.NotConnected();
            }
            catch (IOException)
            {
                OnDisconnection();
                return ReadResult.NotConnected();
            }
            catch (Exception e)
            {
                Utilities.Logger?.Debug($"{_ipAddress}:{Port} ReadAsync threw: {e.Format()}");
                return ReadResult.Fail(e);
            }
            finally
            {
                cts.Dispose();
            }
        }

        public void Reconnect()
        {
            Utilities.Logger?.Debug( $"{_ipAddress}:{Port} Reconnect({ReconnectionPolicy.ShouldReconnect})");
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
                    Utilities.Logger?.Error(e, $"{_ipAddress}:{Port} Reconnection failed");
                }
                _cancelReconnection?.Dispose();
                _cancelReconnection = null;
                //Debug.Print($"{nameof(TcpClient)} Reconnection COMPLETE {Connected}, {IsConnected()}");
            });
        }

        public async Task<WriteResult> WriteAsync(string command, int timeout = -1)
        {
            if (timeout == -1) timeout = WRITE_TIMEOUT;
            if(!Connected) return WriteResult.NotConnected();
            var timeOut = TimeSpan.FromMilliseconds(timeout);
            var taskCompletionSource = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(timeOut);
            try
            {
                var writeTask = _writer.WriteAsync(command);
                await using (cts.Token.Register(() => taskCompletionSource.TrySetResult(true)))
                {
                    if (writeTask != await Task.WhenAny(writeTask, taskCompletionSource.Task))
                    {
                        return WriteResult.Timeout();
                    }
                }
                await writeTask;
                return WriteResult.Success();
            }
            catch (InvalidOperationException)
            {
                Utilities.Logger?.Debug($"{_ipAddress}:{Port} WriteAsync threw {nameof(InvalidOperationException)}, socket {(_tcpClient == null ? "NULL" : _tcpClient.Connected ? "Connected" : "NOT Connected")}");
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
                Utilities.Logger?.Debug($"{_ipAddress}:{Port} WriteAsync threw {e.Format()}");
                return WriteResult.Fail(e);
            }
            finally
            {
                cts.Dispose();
            }
        }
    }
}