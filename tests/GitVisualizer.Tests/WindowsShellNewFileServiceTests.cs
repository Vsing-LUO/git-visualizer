using GitVisualizer.Infrastructure.FileSystem;

namespace GitVisualizer.Tests;

public sealed class WindowsShellNewFileServiceTests
{
    [Fact]
    public async Task DiscoveryReturnsUniqueSafeTypesAndCreatesARegisteredTemplate()
    {
        var service = new WindowsShellNewFileService();
        var types = await service.GetAvailableTypesAsync();

        Assert.Equal(
            types.Count,
            types.Select(type => type.Extension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(types, type =>
        {
            Assert.StartsWith(".", type.Extension);
            Assert.Equal(type.Extension.ToLowerInvariant(), type.Id);
            Assert.EndsWith(type.Extension, type.SuggestedFileName);
        });
        Assert.DoesNotContain(
            types,
            type => type.Extension.Equals(
                ".pdfwpsshellnew",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            types,
            type => type.Extension.Equals(
                ".pptxwpsaicreateshellnew",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            types,
            type => type.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            types,
            type => type.Extension.Equals(".library-ms", StringComparison.OrdinalIgnoreCase));

        var selected = types.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "系统模板" + selected.Extension);
        await service.CreateAsync(temporary.Path, path, selected.Id);

        Assert.True(File.Exists(path));
    }
}
