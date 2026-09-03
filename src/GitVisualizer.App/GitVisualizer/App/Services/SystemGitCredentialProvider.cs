using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitVisualizer.Core;

namespace GitVisualizer.App.Services;

public static class SystemGitCredentialProvider
{
	public static async Task<RemoteCredential?> GetAsync(string remoteUrl, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!IsHttpsAddress(remoteUrl))
		{
			return null;
		}
		try
		{
			ProcessStartInfo processStartInfo = new ProcessStartInfo("git")
			{
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};
			processStartInfo.ArgumentList.Add("credential");
			processStartInfo.ArgumentList.Add("fill");
			using Process process = new Process
			{
				StartInfo = processStartInfo
			};
			if (!process.Start())
			{
				return null;
			}
			await process.StandardInput.WriteAsync(("url=" + remoteUrl + Environment.NewLine + Environment.NewLine).AsMemory(), cancellationToken);
			process.StandardInput.Close();
			Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			string output = await outputTask;
			await errorTask;
			return (process.ExitCode == 0) ? ParseResponse(output) : null;
		}
		catch (Exception ex) when (((ex is Win32Exception || ex is InvalidOperationException || ex is IOException) ? 1 : 0) != 0)
		{
			return null;
		}
	}

	public static RemoteCredential? ParseResponse(string response)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string[] array = response.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string text in array)
		{
			int num = text.IndexOf('=');
			if (num > 0)
			{
				dictionary[text.Substring(0, num)] = text.Substring(num + 1);
			}
		}
		if (!dictionary.TryGetValue("username", out var value) || !dictionary.TryGetValue("password", out var value2) || string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(value2))
		{
			return null;
		}
		return new RemoteCredential(CredentialKind.HttpsToken, value, value2);
	}

	private static bool IsHttpsAddress(string remoteUrl)
	{
		if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out Uri result))
		{
			return result.Scheme == Uri.UriSchemeHttps;
		}
		return false;
	}
}
