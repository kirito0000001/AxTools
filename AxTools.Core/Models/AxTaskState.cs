namespace AxTools.Core.Models;

public enum AxTaskEventType
{
    Stage,
    Progress,
    Artifact,
    Warning,
    Result,
    Cancellation
}

public enum AxTaskStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Stopped
}

public enum AxTaskResultStatus
{
    Succeeded,
    Failed,
    Stopped
}

public enum AxTaskCancellationMode
{
    Cancel,
    Stop,
    Locked
}

public enum AxTaskShutdownDecision
{
    NoActiveTask,
    StopRequested,
    BlockedLocked
}

public enum AxTaskOutputStream
{
    StandardOutput,
    StandardError
}
