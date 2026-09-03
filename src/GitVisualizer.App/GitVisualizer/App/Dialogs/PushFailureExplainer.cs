using System;
using System.Collections.Generic;
using System.Linq;
using GitVisualizer.Core;

namespace GitVisualizer.App.Dialogs;

internal sealed record PushFailureExplanation(string Reason, string Suggestion);

internal static class PushFailureExplainer
{
	public static PushFailureExplanation Explain(GitOperationResult result)
	{
		ArgumentNullException.ThrowIfNull(result);

		string diagnosticText = string.Join(
			'\n',
			DiagnosticParts(result).Where(part => !string.IsNullOrWhiteSpace(part)));

		if (ContainsAny(diagnosticText,
			"non-fast-forward", "non-fastforward", "fetch first",
			"tip of your current branch is behind", "updates were rejected because the remote contains work"))
		{
			return new PushFailureExplanation(
				"远程分支包含本地尚未拥有的提交。为避免覆盖他人的更改，Git 拒绝了这次推送。",
				"请先获取并检查远程更新，再通过拉取合并或变基整合这些提交；确认历史无误后重新推送。");
		}

		if (ContainsAny(diagnosticText,
			"authentication failed", "authentication required", "invalid username or password",
			"invalid credentials", "bad credentials", "http 401", "status code: 401"))
		{
			return new PushFailureExplanation(
				"远程服务未接受当前登录凭据，凭据可能已失效、填写错误或缺少所需权限。",
				"请重新登录或更新访问令牌，并确认令牌拥有该仓库的写入权限后重试。");
		}

		if (ContainsAny(diagnosticText,
			"repository not found", "remote repository not found", "not found: repository"))
		{
			return new PushFailureExplanation(
				"远程仓库地址不存在，或者当前账号无权查看该仓库。部分托管平台会用“仓库不存在”隐藏权限问题。",
				"请核对远程地址，并确认当前账号已被授予该仓库的访问和推送权限。");
		}

		if (ContainsAny(diagnosticText,
			"protected branch", "pre-receive hook declined", "protected branch hook declined",
			"gh006", "gh013", "push rule", "branch policy"))
		{
			return new PushFailureExplanation(
				"远程仓库的分支保护、提交规则或服务端检查拒绝了这次更新。",
				"请按远程平台提示修正提交，或改推到允许的分支并通过合并请求更新目标分支。");
		}

		if (ContainsAny(diagnosticText,
			"permission denied", "access denied", "write access", "not permitted",
			"http 403", "status code: 403"))
		{
			return new PushFailureExplanation(
				"当前账号或 SSH 密钥没有向该远程仓库写入的权限。",
				"请确认登录账号、仓库成员权限和远程地址正确，并为当前凭据开通写入权限。");
		}

		if (ContainsAny(diagnosticText,
			"could not resolve host", "name or service not known", "no such host",
			"temporary failure in name resolution"))
		{
			return new PushFailureExplanation(
				"无法解析远程服务器的域名，通常与网络、DNS、代理设置或远程地址拼写有关。",
				"请检查网络连接和远程地址；如使用代理或 VPN，也请确认其 DNS 与连接设置可用。");
		}

		if (ContainsAny(diagnosticText,
			"failed to connect", "connection timed out", "operation timed out",
			"connection refused", "connection reset", "network is unreachable"))
		{
			return new PushFailureExplanation(
				"程序未能与远程服务器建立或保持网络连接。",
				"请检查网络、代理、VPN 和防火墙设置，确认远程服务可访问后再试。");
		}

		if (ContainsAny(diagnosticText,
			"ssl certificate", "certificate problem", "certificate verify failed",
			"schannel", "tls"))
		{
			return new PushFailureExplanation(
				"远程服务器的 HTTPS/TLS 证书未通过验证，可能是证书过期、系统时间不准或代理替换了证书。",
				"请检查系统时间、代理和证书链；不要通过关闭证书校验来绕过此问题。");
		}

		if (ContainsAny(diagnosticText,
			"host key verification failed", "could not read from remote repository",
			"ssh agent", "publickey"))
		{
			return new PushFailureExplanation(
				"SSH 主机身份或密钥认证失败，当前 SSH 配置无法访问远程仓库。",
				"请确认远程主机指纹可信、SSH Agent 已加载正确密钥，且对应公钥已添加到远程账号。");
		}

		if (ContainsAny(diagnosticText,
			"src refspec", "does not match any", "no commits yet", "unborn branch"))
		{
			return new PushFailureExplanation(
				"Git 找不到要推送的本地分支或提交，常见于新仓库尚未创建首个提交。",
				"请确认当前分支名称正确且至少已有一个提交，然后重新推送。");
		}

		if (ContainsAny(diagnosticText,
			"rpc failed", "remote end hung up unexpectedly", "unexpected disconnect",
			"early eof", "the remote disconnected"))
		{
			return new PushFailureExplanation(
				"传输过程中远程连接意外中断，可能是网络波动、代理限制或服务端暂时异常。",
				"请确认网络稳定后重试；若持续发生，请检查代理限制和远程服务状态。");
		}

		if (ContainsAny(diagnosticText, "cancelled", "canceled", "operation was canceled"))
		{
			return new PushFailureExplanation(
				"推送在完成前被取消，因此远程分支没有按本次操作完成更新。",
				"如果仍需推送，请确认没有其他操作占用仓库后重新执行。");
		}

		return new PushFailureExplanation(
			"Git 未能完成推送，但当前错误不属于程序已识别的常见类型；原始详情已保留在过程记录中。",
			"请先核对远程地址、网络、登录凭据和分支状态；若仍失败，可复制原始错误继续排查。");
	}

	private static IEnumerable<string?> DiagnosticParts(GitOperationResult result)
	{
		yield return result.ErrorCode;
		yield return result.ErrorMessage;
		yield return result.Summary;
		foreach (string detail in result.Details)
		{
			yield return detail;
		}
	}

	private static bool ContainsAny(string source, params string[] values) =>
		values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
}
