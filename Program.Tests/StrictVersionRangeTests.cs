using System.IO;
using Minecraft;

namespace Minecraft.Tests;

public sealed class StrictVersionRangeTests
{
    [Theory]
    [InlineData("[1.20.2,26.4)", "26.3", true)]
    [InlineData("[1.20.2,26.4)", "26.4", false)]
    [InlineData("(,1.18.2],[26.1,26.4)", "26.3", true)]
    [InlineData("(,1.18.2],[26.1,26.4)", "1.21.1", false)]
    [InlineData("[1.21.1]", "1.21.1.0", true)]
    [InlineData("[20.4.72-beta,)", "26.3.0.99", true)]
    [InlineData("[20.4.72-beta,)", "20.4.72-alpha", false)]
    [InlineData(">=1.19 <26.1-", "1.21.11", true)]
    [InlineData(">=1.19 <26.1-", "26.1-alpha.1", false)]
    [InlineData(">=26.1- <26.4-", "26.3", true)]
    [InlineData("1.20.x", "1.20.6", true)]
    [InlineData("1.20.*", "1.21", false)]
    [InlineData("~1.20.1", "1.20.6", true)]
    [InlineData("~1.20.1", "1.21", false)]
    [InlineData("~26", "26.3", true)]
    [InlineData("^0.3.4", "0.3.9", true)]
    [InlineData("^0.3.4", "0.4", false)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("^0.0", "0.0.99", true)]
    [InlineData("^0.0", "0.1", false)]
    [InlineData("^0", "0.99", true)]
    [InlineData("^0", "1.0", false)]
    [InlineData("^1.2.3", "1.9", true)]
    [InlineData("^1.2.3", "2.0-beta", false)]
    [InlineData("=0.29.0-beta.2", "0.29.0-beta.2+local", true)]
    [InlineData("1.20.1 || 1.21.1", "1.21.1", true)]
    [InlineData("1.20.1 || 1.21.1", "26.3", false)]
    public void SupportedPredicates_AreEvaluatedPrecisely(string range, string version, bool expected) =>
        Assert.Equal(expected, StrictVersionRange.Accepts(range, version));

    [Theory]
    [InlineData("")]
    [InlineData("any")]
    [InlineData("latest")]
    [InlineData("26.3-snapshot-?")]
    [InlineData("[1.20.1,]")]
    [InlineData("[1.20.1,26.3")]
    [InlineData("[26.3,1.20.1)")]
    [InlineData("[1.20.1,26.3) garbage")]
    [InlineData("* || garbage")]
    [InlineData("1.20.1 ||")]
    [InlineData(">=1.x")]
    [InlineData("1.*.2")]
    [InlineData("1.20.1 - 1.21.1")]
    [InlineData("1.20.1,")]
    [InlineData("(1.20.1)")]
    public void UnsupportedPredicates_CannotSilentlyMatch(string range) =>
        Assert.Throws<InvalidDataException>(() => StrictVersionRange.Accepts(range, "26.3"));
}
