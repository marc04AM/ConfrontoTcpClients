using Serilog;

namespace Sistec.Core.Utils;

/// <summary>
/// Logger statico globale usato da FaelTcpClient.
/// </summary>
public static class Utilities
{
    public static ILogger? Logger { get; set; }
}
