using System.Security.Cryptography;
using System.Text;

namespace GitVisualizer.App.Services;

public static class RemoteCredentialKey
{
    public static string Create(string remoteUrl)
    {
        var canonicalAddress = Canonicalize(remoteUrl);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalAddress));
        return $"remote-repository:{Convert.ToHexString(digest)}";
    }

    public static string Canonicalize(string remoteUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);
        var candidate = remoteUrl.Trim();

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            var path = NormalizeRepositoryPath(uri.AbsolutePath);
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}/{path}";
        }

        var at = candidate.IndexOf('@');
        var colon = candidate.IndexOf(':', Math.Max(0, at));
        if (at >= 0 && colon > at)
        {
            var host = candidate[(at + 1)..colon].ToLowerInvariant();
            var path = NormalizeRepositoryPath(candidate[(colon + 1)..]);
            return $"ssh://{host}/{path}";
        }

        return candidate.Replace('\\', '/').TrimEnd('/');
    }

    private static string NormalizeRepositoryPath(string path)
    {
        var normalized = Uri.UnescapeDataString(path)
            .Replace('\\', '/')
            .Trim('/');
        return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }
}
