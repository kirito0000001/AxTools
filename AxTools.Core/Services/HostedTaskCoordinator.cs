namespace AxTools.Core.Services;

public sealed class HostedTaskCoordinator
{
    private readonly object _stateLock = new();
    private bool _isBusy;
    private TaskCompletionSource _idleCompletion = CreateCompletedSource();

    public bool IsBusy
    {
        get
        {
            lock (_stateLock)
            {
                return _isBusy;
            }
        }
    }

    public bool TryBegin()
    {
        lock (_stateLock)
        {
            if (_isBusy)
            {
                return false;
            }

            _isBusy = true;
            _idleCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public void End()
    {
        TaskCompletionSource idleCompletion;
        lock (_stateLock)
        {
            _isBusy = false;
            idleCompletion = _idleCompletion;
        }

        idleCompletion.TrySetResult();
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        Task idleTask;
        lock (_stateLock)
        {
            if (!_isBusy)
            {
                return Task.CompletedTask;
            }

            idleTask = _idleCompletion.Task;
        }

        return cancellationToken.CanBeCanceled
            ? idleTask.WaitAsync(cancellationToken)
            : idleTask;
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
