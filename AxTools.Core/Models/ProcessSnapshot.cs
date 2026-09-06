namespace AxTools.Core.Models;

public sealed record ProcessSnapshot(
    int ProcessId,
    string ProcessName,
    string ExecutablePath);
