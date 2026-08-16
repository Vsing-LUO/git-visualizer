using GitVisualizer.App.Services;

namespace GitVisualizer.Tests;

public sealed class RemoteCredentialKeyTests
{
    [Fact]
    public void Create_SeparatesRepositoriesOnTheSameHost()
    {
        var first = RemoteCredentialKey.Create(
            "https://github.com/Vsing-LUO/1111.git");
        var second = RemoteCredentialKey.Create(
            "https://github.com/Vsing-LUO/test-cli.git");

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(
        "https://github.com/Vsing-LUO/1111.git",
        "https://github.com/Vsing-LUO/1111/")]
    [InlineData(
        "git@github.com:Vsing-LUO/1111.git",
        "ssh://git@github.com/Vsing-LUO/1111")]
    public void Create_TreatsEquivalentRepositoryAddressesAsTheSameCredential(
        string firstAddress,
        string secondAddress)
    {
        Assert.Equal(
            RemoteCredentialKey.Create(firstAddress),
            RemoteCredentialKey.Create(secondAddress));
    }
}
