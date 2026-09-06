using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class SettingsPathProviderTests
{
    [Fact]
    public void GetDefaultProjectRoot_UsesAxToolsChineseFolderOnDDrive()
    {
        var provider = new SettingsPathProvider(
            roamingAppDataRoot: @"C:\Users\Test\AppData\Roaming",
            driveExists: path => string.Equals(path, @"D:\", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(@"D:\Ax工具箱项目", provider.GetDefaultProjectRoot());
        Assert.Equal(
            @"C:\Users\Test\AppData\Roaming\AxTools\bootstrap.json",
            provider.GetBootstrapPath());
    }

    [Fact]
    public void GetDefaultProjectRoot_ThrowsWhenDDriveIsUnavailable()
    {
        var provider = new SettingsPathProvider(@"C:\AppData", _ => false);
        Assert.Throws<DriveNotFoundException>(() => provider.GetDefaultProjectRoot());
    }
}
