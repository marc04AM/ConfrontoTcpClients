using Sistec.Core.Interfaces;

namespace Sistec.Core.Utils;

/// <summary>
/// Policy di riconnessione base usata da FaelTcpClient.
/// Default: ShouldReconnect = false per evitare loop nei test.
/// </summary>
public class ReconnectionPolicy : IReconnectionPolicy
{
    public bool ShouldReconnect { get; set; }
    public TimeSpan GetNextDelay(int attempt) => TimeSpan.FromSeconds(1);

    public static ReconnectionPolicy Default => new() { ShouldReconnect = false };
}
