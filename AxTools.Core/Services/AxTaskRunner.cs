using AxTools.Core.Models;

namespace AxTools.Core.Services;

public sealed class AxTaskRunner
{
    private readonly IAxProcessHost _processHost;
    private readonly AxTaskEventParser _parser;
    private readonly SemaphoreSlim _heavyTaskGate = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _currentCancellation;
    private AxTaskDefinition? _currentTask;
    private AxTaskCancellationMode _currentCancellationMode = AxTaskCancellationMode.Cancel;

    public AxTaskRunner(
        IAxProcessHost processHost,
        AxTaskEventParser parser)
    {
        _processHost = processHost;
        _parser = parser;
    }

    public event Action<AxTaskEvent>? EventReceived;

    public event Action<AxTaskParsedLine>? OutputReceived;

    public event Action<AxTaskStatus>? StatusChanged;

    public AxTaskDefinition? CurrentTask
    {
        get
        {
            lock (_stateLock)
            {
                return _currentTask;
            }
        }
    }

    public AxTaskCancellationMode CurrentCancellationMode
    {
        get
        {
            lock (_stateLock)
            {
                return _currentCancellationMode;
            }
        }
    }

    public AxTaskShutdownDecision RequestShutdown()
    {
        lock (_stateLock)
        {
            if (_currentTask is null ||
                _currentCancellation is null)
            {
                return AxTaskShutdownDecision.NoActiveTask;
            }

            if (_currentCancellationMode == AxTaskCancellationMode.Locked)
            {
                return AxTaskShutdownDecision.BlockedLocked;
            }

            _currentCancellation.Cancel();
            return AxTaskShutdownDecision.StopRequested;
        }
    }

    public bool TryRequestStop() =>
        RequestShutdown() == AxTaskShutdownDecision.StopRequested;

    public async Task<AxTaskResult> RunAsync(
        AxTaskDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var gateAcquired = false;
        if (definition.IsHeavy)
        {
            StatusChanged?.Invoke(AxTaskStatus.Queued);
            await _heavyTaskGate.WaitAsync(cancellationToken);
            gateAcquired = true;
        }

        var startedAt = DateTimeOffset.Now;
        var output = new List<string>();
        var events = new List<AxTaskEvent>();
        AxTaskResultStatus? reportedResult = null;
        var protocolErrorCount = 0;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        lock (_stateLock)
        {
            _currentTask = definition;
            _currentCancellation = linkedCancellation;
            _currentCancellationMode = AxTaskCancellationMode.Cancel;
        }

        StatusChanged?.Invoke(AxTaskStatus.Running);

        try
        {
            async ValueTask HandleLine(AxTaskOutputStream stream, string line)
            {
                await Task.CompletedTask;
                var parsed = _parser.Parse(line);
                output.Add(parsed.Text);

                if (parsed.Event is { } taskEvent)
                {
                    events.Add(taskEvent);
                    if (taskEvent.ResultStatus is { } resultStatus)
                    {
                        reportedResult = resultStatus;
                    }

                    if (taskEvent.CancellationMode is { } cancellationMode)
                    {
                        lock (_stateLock)
                        {
                            if (ReferenceEquals(_currentTask, definition))
                            {
                                _currentCancellationMode = cancellationMode;
                            }
                        }
                    }

                    EventReceived?.Invoke(taskEvent);
                }
                else
                {
                    if (parsed.ProtocolError is not null)
                    {
                        protocolErrorCount++;
                    }

                    OutputReceived?.Invoke(parsed);
                }
            }

            AxProcessResult processResult;
            try
            {
                processResult = await _processHost.RunAsync(
                    definition,
                    HandleLine,
                    linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                processResult = new AxProcessResult(130, CancellationRequested: true);
            }

            var status = DetermineStatus(
                processResult.ExitCode,
                processResult.CancellationRequested,
                reportedResult);
            StatusChanged?.Invoke(status);

            return new AxTaskResult(
                definition.Id,
                status,
                processResult.ExitCode,
                startedAt,
                DateTimeOffset.Now,
                CreateSummary(status, processResult.ExitCode, protocolErrorCount),
                output,
                events);
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_currentTask, definition))
                {
                    _currentTask = null;
                    _currentCancellation = null;
                    _currentCancellationMode = AxTaskCancellationMode.Cancel;
                }
            }

            if (gateAcquired)
            {
                _heavyTaskGate.Release();
            }
        }
    }

    private static AxTaskStatus DetermineStatus(
        int exitCode,
        bool cancellationRequested,
        AxTaskResultStatus? reportedResult)
    {
        if (reportedResult == AxTaskResultStatus.Stopped || cancellationRequested)
        {
            return AxTaskStatus.Stopped;
        }

        return exitCode == 0 && reportedResult == AxTaskResultStatus.Succeeded
            ? AxTaskStatus.Succeeded
            : AxTaskStatus.Failed;
    }

    private static string CreateSummary(
        AxTaskStatus status,
        int exitCode,
        int protocolErrorCount)
    {
        var summary = status switch
        {
            AxTaskStatus.Succeeded => "任务已成功完成。",
            AxTaskStatus.Stopped => "任务已停止，已生成内容可能保留。",
            _ => $"任务执行失败，退出码 {exitCode}。"
        };

        return protocolErrorCount == 0
            ? summary
            : $"{summary} 已忽略 {protocolErrorCount} 条无效协议事件。";
    }
}
