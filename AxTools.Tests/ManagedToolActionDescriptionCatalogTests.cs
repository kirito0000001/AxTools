using AxTools.Core.Models;
using Xunit;

namespace AxTools.Tests;

public sealed class ManagedToolActionDescriptionCatalogTests
{
    [Fact]
    public void EveryManagedActionHasAReadableDescription()
    {
        foreach (var action in Enum.GetValues<ManagedToolAction>())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(ManagedToolActionDescriptionCatalog.GetDescription(action)),
                $"{action} 缺少功能说明。");
        }
    }

    [Theory]
    [InlineData(ManagedToolAction.UploadDryRun)]
    [InlineData(ManagedToolAction.PublishDryRun)]
    public void DryRunDescriptionsExplicitlyPromiseNoRemoteWrites(ManagedToolAction action)
    {
        var description = ManagedToolActionDescriptionCatalog.GetDescription(action);

        Assert.Contains("不上传", description);
        Assert.Contains("不修改远端", description);
    }
}
