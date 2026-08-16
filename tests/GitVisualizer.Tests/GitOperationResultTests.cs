using GitVisualizer.Core;

namespace GitVisualizer.Tests;

public sealed class GitOperationResultTests
{
    [Fact]
    public void Fail_PreservesExceptionMessageForOperationLog()
    {
        var result = GitOperationResult.Fail(
            "push",
            "git push origin",
            new InvalidOperationException("authentication failed"));

        Assert.False(result.Success);
        Assert.Equal("authentication failed", result.ErrorMessage);
        Assert.Equal(["authentication failed"], result.Details);
    }

    [Fact]
    public void OperationLogEntry_FormatsDetailsForDisplay()
    {
        var entry = new OperationLogEntry(
            "id",
            DateTimeOffset.Now,
            "repository",
            "push",
            false,
            GitOperationRisk.Safe,
            "failed",
            "git push origin",
            null,
            "error",
            ["first", "second"]);

        Assert.Equal(
            $"first{Environment.NewLine}second",
            entry.DetailsText);
    }
}
