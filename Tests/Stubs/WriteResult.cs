namespace Sistec.Core.Utils;

public class WriteResult
{
    public bool IsSuccess { get; private set; }
    public bool IsTimeout { get; private set; }
    public bool IsNotConnected { get; private set; }
    public Exception? Exception { get; private set; }
    private string _status = "";

    private WriteResult() { }

    public static WriteResult Success() => new() { IsSuccess = true, _status = "Success" };
    public static WriteResult Timeout() => new() { IsTimeout = true, _status = "Timeout" };
    public static WriteResult NotConnected() => new() { IsNotConnected = true, _status = "NotConnected" };
    public static WriteResult Fail(Exception e) => new() { Exception = e, _status = "Fail" };

    public override string ToString() => _status;
}
