using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using GitVisualizer.Core;

namespace GitVisualizer.App.Services;

public static class SystemGitCredentialProvider
{
    public static async Task<RemoteCredential?> GetAsync(
        string remoteUrl,
        CancellationToken cancellationToken = default)
    {
        if (!IsHttpsAddress(remoteUrl))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("credential");
            startInfo.ArgumentList.Add("fill");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            // The secret is returned over redirected standard output. It is never
            // placed in the process arguments, application logs, or UI.
            await process.StandardInput.WriteAsync(
                $"url={remoteUrl}{Environment.NewLine}{Environment.NewLine}"
                    .AsMemory(),
                cancellationToken);
            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            await errorTask;

            return process.ExitCode == 0 ? ParseResponse(output) : null;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static RemoteCredential? ParseResponse(string response)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in response.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line[..separator]] = line[(separator + 1)..];
            }
        }

        return values.TryGetValue("username", out var userName) &&
               values.TryGetValue("password", out var password) &&
               !string.IsNullOrWhiteSpace(userName) &&
               !string.IsNullOrWhiteSpace(password)
            ? new RemoteCredential(
                CredentialKind.HttpsToken,
                userName,
                password,
                Remember: false)
            : null;
    }

    private static bool IsHttpsAddress(string remoteUrl) =>
        Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
