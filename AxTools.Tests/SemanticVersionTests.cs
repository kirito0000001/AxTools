using AxTools.Core.Services;
using Xunit;

namespace AxTools.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.1-beta.1", "1.0.0", 1)]
    [InlineData("1.0.1", "1.0.1-beta.1", 1)]
    [InlineData("1.0.1-beta.2", "1.0.1-beta.1", 1)]
    [InlineData("v1.0.1-beta.1", "1.0.1-beta.1", 0)]
    public void CompareTo_UsesSemanticVersionOrder(
        string left,
        string right,
        int expectedSign)
    {
        var comparison = SemanticVersion.Parse(left).CompareTo(
            SemanticVersion.Parse(right));

        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0-")]
    [InlineData("version-one")]
    public void Parse_RejectsInvalidVersion(string value)
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(value));
    }
}
