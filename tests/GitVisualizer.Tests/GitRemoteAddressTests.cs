using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class GitRemoteAddressTests
{
    [Theory]
    [InlineData(
        "//github.com/Vsing-LUO/1111.git",
        "https://github.com/Vsing-LUO/1111.git")]
    [InlineData(
        " https://github.com/Vsing-LUO/1111.git ",
        "https://github.com/Vsing-LUO/1111.git")]
    [InlineData(
        "ssh://git@github.com/Vsing-LUO/1111.git",
        "ssh://git@github.com/Vsing-LUO/1111.git")]
    [InlineData(
        "git@github.com:Vsing-LUO/1111.git",
        "git@github.com:Vsing-LUO/1111.git")]
    public void TryNormalize_AcceptsCommonRemoteAddresses(
        string value,
        string expected)
    {
        var valid = GitRemoteAddress.TryNormalize(value, out var normalized);

        Assert.True(valid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("github.com/Vsing-LUO/1111.git")]
    [InlineData("https:///1111.git")]
    [InlineData("javascript:alert(1)")]
	[InlineData("https://username:token@example.com/repository.git")]
	[InlineData("http://username:password@example.com/repository.git")]
	[InlineData("//username:token@example.com/repository.git")]
    public void TryNormalize_RejectsMalformedOrUnsupportedAddresses(string value)
    {
        var valid = GitRemoteAddress.TryNormalize(value, out var normalized);

        Assert.False(valid);
        Assert.Empty(normalized);
    }
}
