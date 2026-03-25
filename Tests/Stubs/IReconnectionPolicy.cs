namespace Sistec.Core.Interfaces;

public interface IReconnectionPolicy
{
    bool ShouldReconnect { get; }
    TimeSpan GetNextDelay(int attempt);
}
