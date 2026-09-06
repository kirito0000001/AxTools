using AxTools.Core.Models;

namespace AxTools.Core.Adapters;

public abstract class ManagedToolAdapterBase : IManagedToolAdapter
{
    private readonly string _wrapperPath;
    private readonly IReadOnlyList<string> _requiredRelativePaths;

    protected ManagedToolAdapterBase(
        ManagedToolKey key,
        string scriptsRoot,
        string wrapperDirectory,
        IReadOnlyList<ManagedToolActionDefinition> actions,
        params string[] requiredRelativePaths)
    {
        Key = key;
        Actions = actions;
        _requiredRelativePaths = requiredRelativePaths;
        _wrapperPath = Path.GetFullPath(Path.Combine(
            scriptsRoot,
            "Adapters",
            wrapperDirectory,
            $"Invoke-{wrapperDirectory}Action.ps1"));
    }

    public ManagedToolKey Key { get; }

    public IReadOnlyList<ManagedToolActionDefinition> Actions { get; }

    public virtual ManagedToolValidationResult Validate(ManagedToolPaths paths)
    {
        if (string.IsNullOrWhiteSpace(paths.SourceRoot))
        {
            return ManagedToolValidationResult.Failure("尚未配置源码目录。");
        }

        string sourceRoot;
        try
        {
            sourceRoot = Path.GetFullPath(paths.SourceRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ManagedToolValidationResult.Failure("源码目录格式无效。");
        }

        var missing = _requiredRelativePaths
            .Where(relativePath => !File.Exists(Path.Combine(sourceRoot, relativePath)))
            .ToArray();
        return missing.Length == 0
            ? ManagedToolValidationResult.Success("项目特征检查通过。")
            : ManagedToolValidationResult.Failure(
                $"缺少项目特征：{string.Join("、", missing)}",
                missing);
    }

    public virtual AxTaskDefinition CreateTask(
        ManagedToolActionRequest request,
        ManagedToolPaths paths)
    {
        var action = Actions.SingleOrDefault(item => item.Action == request.Action)
            ?? throw new NotSupportedException($"{Key} 不支持动作 {request.Action}。");
        var arguments = new List<string>
        {
            "-Action",
            request.Action.ToString(),
            "-ProjectRoot",
            paths.SourceRoot,
            "-DevelopmentExecutable",
            paths.DevelopmentExecutable,
            "-ReleaseExecutable",
            paths.ReleaseExecutable,
            "-OutputRoot",
            paths.OutputRoot,
            "-Version",
            request.Version,
            "-Channel",
            request.Channel
        };

        if (request.Action is ManagedToolAction.UploadDryRun or ManagedToolAction.PublishDryRun)
        {
            arguments.Add("-DryRun");
        }

        return new AxTaskDefinition(
            $"{Key}-{request.Action}-{Guid.NewGuid():N}",
            $"{GetDisplayName()} · {action.DisplayName}",
            _wrapperPath,
            arguments,
            action.IsHeavy);
    }

    protected abstract string GetDisplayName();

    protected static ManagedToolActionDefinition Action(
        ManagedToolAction action,
        string name,
        ManagedToolActionSection section,
        string glyph,
        bool isHeavy,
        bool requiresVersion = false,
        bool requiresConfirmation = false) =>
        new(
            action,
            name,
            section,
            glyph,
            isHeavy,
            requiresVersion,
            requiresConfirmation);
}
