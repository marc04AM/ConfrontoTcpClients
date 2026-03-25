using Sistec.Core.Interfaces;

namespace Sistec.Core.Utils;

public class ReconnectAgent
{
    public async Task ReconnectAsync(Func<Task<bool>> reconnectFunc, CancellationToken token, IReconnectionPolicy policy)
    {
        int attempt = 0;
        while (!token.IsCancellationRequested)
        {
            var delay = policy.GetNextDelay(attempt++);
            await Task.Delay(delay, token);
            if (await reconnectFunc()) return;
        }
    }
}
