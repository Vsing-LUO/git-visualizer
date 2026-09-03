using System.Text.RegularExpressions;

namespace GitVisualizer.Core;

public static partial class GitRemoteAddress
{
    private static readonly HashSet<string> SupportedSchemes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            Uri.UriSchemeHttp,
            Uri.UriSchemeHttps,
            Uri.UriSchemeFile,
            "git",
            "ssh"
        };

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Any(char.IsControl))
        {
            return false;
        }

        // A pasted address such as //github.com/owner/repository.git is a
        // scheme-relative web URL. Git otherwise treats it as a filesystem path.
        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = $"https:{candidate}";
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (!SupportedSchemes.Contains(uri.Scheme))
            {
                return false;
            }

            if ((uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            if (!uri.IsFile && string.IsNullOrWhiteSpace(uri.Host))
            {
                return false;
            }

            normalized = candidate;
            return true;
        }

        if (ScpStyleAddress().IsMatch(candidate) ||
            Path.IsPathFullyQualified(candidate))
        {
            normalized = candidate;
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^[^@\s/:]+@[^:\s/]+:\S+$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpStyleAddress();
}
