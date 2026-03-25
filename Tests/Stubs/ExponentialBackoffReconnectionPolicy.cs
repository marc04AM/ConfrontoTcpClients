using Sistec.Core.Interfaces;

namespace Sistec.Core.Utils;

/// <summary>
/// Policy con backoff esponenziale usata da SiDelTcpClient e MbTcpClient.
/// Default: ShouldReconnect = false per evitare loop nei test.
/// </summary>
public class ExponentialBackoffReconnectionPolicy : IReconnectionPolicy
{
    public bool ShouldReconnect { get; set; }
    public TimeSpan GetNextDelay(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    public static ExponentialBackoffReconnectionPolicy Default => new() { ShouldReconnect = false };
}
