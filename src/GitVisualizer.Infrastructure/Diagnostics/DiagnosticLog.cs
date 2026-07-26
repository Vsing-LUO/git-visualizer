using System.Text.RegularExpressions;

namespace GitVisualizer.Infrastructure.Diagnostics;

public static partial class DiagnosticLog
{
    private static readonly object Gate = new();

    public static void Initialize()
    {
        LocalPaths.EnsureCreated();
        foreach (var file in Directory.EnumerateFiles(LocalPaths.LogDirectory, "gitvisualizer-*.log"))
        {
            var info = new FileInfo(file);
            if (DateTimeOffset.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(14))
            {
                File.Delete(file);
            }
        }
    }

    public static void Write(string category, Exception exception)
    {
        Initialize();
        var text = $"{DateTimeOffset.Now:O} [{Sanitize(category)}] {Sanitize(exception.ToString())}";
        lock (Gate)
        {
            File.AppendAllText(
                Path.Combine(LocalPaths.LogDirectory, $"gitvisualizer-{DateTimeOffset.Now:yyyyMMdd}.log"),
                text + Environment.NewLine);
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = CredentialInUrlRegex().Replace(value, "${scheme}***@");
        sanitized = TokenAssignmentRegex().Replace(sanitized, "${name}=***");
        return PrivateKeyRegex().Replace(
            sanitized,
            "-----BEGIN PRIVATE KEY-----***-----END PRIVATE KEY-----");
    }

    [GeneratedRegex(@"(?<scheme>https?://)[^/\s:@]+:[^@\s/]+@", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialInUrlRegex();

    [GeneratedRegex(@"(?<name>token|password|passphrase|secret)\s*[=:]\s*[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenAssignmentRegex();

    [GeneratedRegex(
        @"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRegex();
}
