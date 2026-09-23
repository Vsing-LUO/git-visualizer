// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using LibGit2Sharp;

namespace GitVisualizer.Tests;

internal static class TestDataIsolation
{
    internal static string GlobalConfigFile { get; private set; } = null!;
    // Also isolates directly invoked IDE/dotnet test runs. Retain data for investigation.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var parent = Environment.GetEnvironmentVariable("GITVISUALIZER_DATA_ROOT") ??
            Path.Combine(Path.GetTempPath(), "GitVisualizer.Tests");
        Environment.SetEnvironmentVariable("GITVISUALIZER_DATA_ROOT",
            Path.Combine(parent, "session-" + Guid.NewGuid().ToString("N")));
        // A regression in a write guard must still never target the developer's configuration.
        var configDirectory = Path.Combine(Environment.GetEnvironmentVariable("GITVISUALIZER_DATA_ROOT")!, "git-config");
        Directory.CreateDirectory(configDirectory);
        GlobalConfigFile = Path.Combine(configDirectory, ".gitconfig");
        // Pin the Windows baseline's line-ending policy instead of inheriting this machine's setting.
        File.WriteAllText(GlobalConfigFile, "[user]\n\tname = Isolation Sentinel\n\temail = sentinel@example.invalid\n[core]\n\tautocrlf = true\n");
        foreach (var level in new[] { ConfigurationLevel.Global, ConfigurationLevel.Xdg, ConfigurationLevel.System })
            GlobalSettings.SetConfigSearchPaths(level, configDirectory);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", GlobalConfigFile);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
    }
}
