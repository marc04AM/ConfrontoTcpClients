namespace Sistec.Core.Utils;

public class ConnectResult
{
    public bool IsConnected { get; private set; }
    public bool IsTimeout { get; private set; }
    public Exception? Exception { get; private set; }
    private string _status = "";

    private ConnectResult() { }

    public static ConnectResult Success() => new() { IsConnected = true, _status = "Success" };
    public static ConnectResult AlreadyConnected() => new() { IsConnected = true, _status = "AlreadyConnected" };
    public static ConnectResult NotConnected() => new() { _status = "NotConnected" };
    public static ConnectResult Timeout() => new() { IsTimeout = true, _status = "Timeout" };
    public static ConnectResult Fail(Exception e) => new() { Exception = e, _status = "Fail" };

    public override string ToString() => _status;
}
