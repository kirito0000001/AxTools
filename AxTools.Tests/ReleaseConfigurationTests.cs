using System.Xml.Linq;
using Xunit;

namespace AxTools.Tests;

public sealed class ReleaseConfigurationTests
{
    [Fact]
    public void Project_UsesSelfContainedRuntimeForDirectReleaseLaunch()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            ".."));
        var document = XDocument.Load(Path.Combine(projectRoot, "AxTools.csproj"));
        var selfContained = document
            .Descendants("SelfContained")
            .Select(element => element.Value)
            .SingleOrDefault();

        Assert.Equal("true", selfContained, ignoreCase: true);
    }
}
