using System;

namespace GitVisualizer.App.Services;

public static class RemoteUrlSecurity
{
	public static bool IsHttps(string remoteUrl)
	{
		return Uri.TryCreate(remoteUrl, UriKind.Absolute, out Uri? uri) &&
			uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
			!string.IsNullOrWhiteSpace(uri.Host) &&
			string.IsNullOrEmpty(uri.UserInfo);
	}
}
