namespace AxTools.Core.Services;

public interface IAtomicTextFileService
{
    Task WriteTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken);
}
