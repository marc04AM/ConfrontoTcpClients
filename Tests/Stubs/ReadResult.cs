namespace Sistec.Core.Utils;

public class ReadResult
{
    public bool IsSuccess { get; private set; }
    public bool IsTimeout { get; private set; }
    public bool IsNotConnected { get; private set; }
    public bool IsNoData { get; private set; }
    public string? Data { get; private set; }
    public Exception? Exception { get; private set; }
    private string _status = "";

    private ReadResult() { }

    public static ReadResult Success(string data) => new() { IsSuccess = true, Data = data, _status = "Success" };
    public static ReadResult Timeout() => new() { IsTimeout = true, _status = "Timeout" };
    public static ReadResult NotConnected() => new() { IsNotConnected = true, _status = "NotConnected" };
    public static ReadResult NoData() => new() { IsNoData = true, _status = "NoData" };
    public static ReadResult Fail(Exception e) => new() { Exception = e, _status = "Fail" };

    public override string ToString() => _status;
}
