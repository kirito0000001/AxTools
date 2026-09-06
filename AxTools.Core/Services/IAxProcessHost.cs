using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed record AxProcessResult(
    int ExitCode,
    bool CancellationRequested);

public interface IAxProcessHost
{
    Task<AxProcessResult> RunAsync(
        AxTaskDefinition definition,
        Func<AxTaskOutputStream, string, ValueTask> onLine,
        CancellationToken cancellationToken);
}
