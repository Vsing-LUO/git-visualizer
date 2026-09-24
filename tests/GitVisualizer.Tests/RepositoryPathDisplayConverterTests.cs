// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using GitVisualizer.App.Converters;
using System.Globalization;
using System.Windows.Data;

namespace GitVisualizer.Tests;

public sealed class RepositoryPathDisplayConverterTests
{
    private static readonly RepositoryPathDisplayConverter Converter = new();

    [Theory]
    [InlineData(@"\~path\Desktop\git测试", "git测试")]
    [InlineData(@"\~path\Desktop\git可视化", "git可视化")]
    [InlineData(@"D:\projects\repository with spaces", "repository with spaces")]
    public void Convert_ShowsOnlyRepositoryFolderName(string path, string expected)
    {
        Assert.Equal(
            expected,
            Converter.Convert(path, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_DoesNotReplaceFullRepositoryPath()
    {
        Assert.Same(
            Binding.DoNothing,
            Converter.ConvertBack("display name", typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
