using Sistec.Core;
using TcpClientEvolution.Tests.Helpers;
using Xunit;

namespace TcpClientEvolution.Tests.DefClientTests;

/// <summary>
/// Test su Use(ILogger) e structured logging.
/// </summary>
public class D08_LoggerTests
{
    [Fact]
    public void Use_AccettaLogger()
    {
        var client = new DefTcpClient();
        var ex = Record.Exception(() => client.Use(null!));
        // Non deve lanciare: il logger è nullable
        Assert.Null(ex);
    }

    [Fact]
    public void Use_ImpostaLoggerInterno()
    {
        var client = new DefTcpClient();
        var logger = new FakeLogger();
        client.Use(logger);

        var storedLogger = ReflectionHelper.GetField<Serilog.ILogger>(client, "_logger");
        Assert.Same(logger, storedLogger);
    }

    [Fact]
    public void LoggerField_UsaSerilog()
    {
        // Verifica strutturale: il campo _logger è di tipo Serilog.ILogger
        var field = typeof(DefTcpClient).GetField("_logger",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(Serilog.ILogger), field!.FieldType);
    }

    [Fact]
    public void LockField_UsaSystemThreadingLock()
    {
        var field = typeof(DefTcpClient).GetField("_lock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Equal(typeof(System.Threading.Lock), field!.FieldType);
    }
}

internal class FakeLogger : Serilog.ILogger
{
    public void Write(Serilog.Events.LogEvent logEvent) { }
}
