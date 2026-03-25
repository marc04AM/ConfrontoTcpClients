using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TcpClientEvolution.Tests.Stubs;

/// <summary>
/// Sink Serilog che cattura tutti i LogEvent in memoria per le asserzioni nei test.
/// </summary>
public class CapturingSink : ILogEventSink
{
    public List<LogEvent> Events { get; } = new();
    public void Emit(LogEvent logEvent) => Events.Add(logEvent);
}

public static class FakeLoggerFactory
{
    public static (ILogger logger, CapturingSink sink) Create()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }
}
