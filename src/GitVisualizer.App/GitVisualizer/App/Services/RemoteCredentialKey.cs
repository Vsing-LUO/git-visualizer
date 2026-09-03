using System;
using System.Security.Cryptography;
using System.Text;

namespace GitVisualizer.App.Services;

public static class RemoteCredentialKey
{
	public static string Create(string remoteUrl)
	{
		string s = Canonicalize(remoteUrl);
		byte[] inArray = SHA256.HashData(Encoding.UTF8.GetBytes(s));
		return "remote-repository:" + Convert.ToHexString(inArray);
	}

	public static string Canonicalize(string remoteUrl)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl, "remoteUrl");
		string text = remoteUrl.Trim();
		if (Uri.TryCreate(text, UriKind.Absolute, out Uri result) && !string.IsNullOrWhiteSpace(result.Host))
		{
			string value = NormalizeRepositoryPath(result.AbsolutePath);
			string value2 = (result.IsDefaultPort ? string.Empty : $":{result.Port}");
			return $"{result.Scheme.ToLowerInvariant()}://{result.Host.ToLowerInvariant()}{value2}/{value}";
		}
		int num = text.IndexOf('@');
		int num2 = text.IndexOf(':', Math.Max(0, num));
		if (num >= 0 && num2 > num)
		{
			int num3 = num + 1;
			string text2 = text.Substring(num3, num2 - num3).ToLowerInvariant();
			string text3 = NormalizeRepositoryPath(text.Substring(num2 + 1));
			return "ssh://" + text2 + "/" + text3;
		}
		return text.Replace('\\', '/').TrimEnd('/');
	}

	private static string NormalizeRepositoryPath(string path)
	{
		string text = Uri.UnescapeDataString(path).Replace('\\', '/').Trim('/');
		if (!text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		string text2 = text;
		return text2.Substring(0, text2.Length - 4);
	}
}
