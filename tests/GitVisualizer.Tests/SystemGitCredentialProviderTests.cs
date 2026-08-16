using GitVisualizer.App.Services;
using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class SystemGitCredentialProviderTests
{
    [Fact]
    public void ParseResponse_ReturnsSystemHttpsCredentialWithoutPersistingIt()
    {
        var credential = SystemGitCredentialProvider.ParseResponse(
            """
            protocol=https
            host=github.com
            username=octocat
            password=oauth-token
            """);

        Assert.NotNull(credential);
        Assert.Equal(CredentialKind.HttpsToken, credential.Kind);
        Assert.Equal("octocat", credential.UserName);
        Assert.Equal("oauth-token", credential.Secret);
        Assert.False(credential.Remember);
    }

    [Fact]
    public void ParseResponse_PreservesEqualsCharactersInsideSecret()
    {
        var credential = SystemGitCredentialProvider.ParseResponse(
            "username=octocat\npassword=part-one=part-two\n");

        Assert.NotNull(credential);
        Assert.Equal("part-one=part-two", credential.Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("username=octocat")]
    [InlineData("password=oauth-token")]
    public void ParseResponse_RejectsIncompleteResponses(string response)
    {
        Assert.Null(SystemGitCredentialProvider.ParseResponse(response));
    }
}
