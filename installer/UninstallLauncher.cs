using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("GitVisualizer Uninstaller")]
[assembly: AssemblyDescription("GitVisualizer user-facing uninstall launcher")]
[assembly: AssemblyCompany("GitVisualizer")]
[assembly: AssemblyProduct("GitVisualizer")]
[assembly: AssemblyCopyright("Copyright (C) 2026 GitVisualizer")]
[assembly: AssemblyVersion("1.3.2.0")]
[assembly: AssemblyFileVersion("1.3.2.0")]

namespace GitVisualizer.UninstallLauncher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string engineDirectory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    ".uninstall");
                string enginePath = FindUninstallEngine(engineDirectory);

                if (enginePath == null)
                {
                    MessageBox.Show(
                        "找不到 GitVisualizer 的内部卸载组件。请重新运行安装程序进行修复。",
                        "GitVisualizer 卸载",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return 2;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = enginePath;
                startInfo.Arguments = BuildArgumentString(args);
                startInfo.WorkingDirectory = engineDirectory;
                startInfo.UseShellExecute = true;

                Process.Start(startInfo);
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "无法启动 GitVisualizer 卸载程序。\r\n\r\n" + exception.Message,
                    "GitVisualizer 卸载",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }

        private static string FindUninstallEngine(string directory)
        {
            string preferredPath = Path.Combine(directory, "unins000.exe");
            if (File.Exists(preferredPath))
            {
                return preferredPath;
            }

            if (!Directory.Exists(directory))
            {
                return null;
            }

            string[] candidates = Directory.GetFiles(directory, "unins*.exe");
            Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
            return candidates.Length == 0 ? null : candidates[candidates.Length - 1];
        }

        private static string BuildArgumentString(string[] args)
        {
            StringBuilder result = new StringBuilder();
            for (int index = 0; index < args.Length; index++)
            {
                if (index > 0)
                {
                    result.Append(' ');
                }

                result.Append(QuoteArgument(args[index]));
            }

            return result.ToString();
        }

        private static string QuoteArgument(string value)
        {
            if (value.Length > 0 &&
                value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return value;
            }

            StringBuilder result = new StringBuilder();
            result.Append('"');
            int backslashCount = 0;

            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    result.Append('\\', (backslashCount * 2) + 1);
                    result.Append('"');
                    backslashCount = 0;
                    continue;
                }

                result.Append('\\', backslashCount);
                backslashCount = 0;
                result.Append(character);
            }

            result.Append('\\', backslashCount * 2);
            result.Append('"');
            return result.ToString();
        }
    }
}
