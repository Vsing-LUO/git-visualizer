// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("卸载 GitVisualizer")]
[assembly: AssemblyProduct("GitVisualizer")]
[assembly: AssemblyCompany("GitVisualizer")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: AssemblyVersion("2.0.0.0")]

internal static class UninstallLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool cleanup = args.Length == 1 && args[0] == "--cleanup";
        bool check = args.Length == 1 && args[0] == "--check";
        try
        {
            if (cleanup || check)
            {
                foreach (var process in Process.GetProcessesByName("GitVisualizer"))
                    using (process) { if (!process.HasExited) throw new IOException("请先关闭所有 GitVisualizer 窗口，然后重新卸载。"); }
                if (cleanup) CleanApplicationData();
                return 0;
            }
            string engine = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".uninstall", "unins000.exe");
            if (!File.Exists(engine)) throw new FileNotFoundException("找不到内部卸载组件，请重新运行 Setup 修复安装。", engine);
            var quoted = new string[args.Length];
            for (int i = 0; i < args.Length; i++) quoted[i] = Quote(args[i]);
            Process.Start(new ProcessStartInfo(engine, string.Join(" ", quoted)) { UseShellExecute = true });
            return 0; // Do not keep this installed launcher locked during uninstall.
        }
        catch (Exception error)
        {
            if (cleanup || check)
            {
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".uninstall", "cleanup-error.txt"), error.Message, Encoding.UTF8); } catch { }
            }
            else MessageBox.Show(error.Message, "卸载 GitVisualizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void CleanApplicationData()
    {
        string local, temp, prefix;
#if CLEANUP_TEST
        string test = Environment.GetEnvironmentVariable("GV_UNINSTALL_TEST_ROOT");
        if (string.IsNullOrEmpty(test) || !Path.IsPathRooted(test)) throw new IOException("Missing isolated test root.");
        local = Path.GetFullPath(test);
        temp = local;
        prefix = "GitVisualizer.UninstallTest:";
#else
        local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        temp = Path.GetTempPath();
        prefix = "GitVisualizer:";
#endif
        DeleteOwnedTree(local, "GitVisualizer");
        DeleteOwnedTree(Path.Combine(temp, ".net"), "GitVisualizer");
        DeleteCredentials(prefix);
    }

    private static void DeleteOwnedTree(string parent, string name)
    {
        string boundary = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(parent, name));
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || name != "GitVisualizer")
            throw new IOException("拒绝清理应用数据范围之外的路径。");
        // Never walk through a redirected parent such as a junction at .net.
        for (string p = Path.GetFullPath(parent); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("数据目录的父目录为链接，请手动核对后清理：" + p);
        if (Directory.Exists(target)) DeleteTreeWithoutFollowingLinks(target);
    }

    private static void DeleteTreeWithoutFollowingLinks(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path, false); return; }
        foreach (string file in Directory.GetFiles(path))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReparsePoint) == 0) File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }
        foreach (string child in Directory.GetDirectories(path)) DeleteTreeWithoutFollowingLinks(child);
        Directory.Delete(path, false);
    }

    private static void DeleteCredentials(string prefix)
    {
        uint count; IntPtr entries;
        if (!CredEnumerate(prefix + "*", 0, out count, out entries))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error);
            return;
        }
        try
        {
            for (int i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(entries, i * IntPtr.Size);
                var credential = (Credential)Marshal.PtrToStructure(pointer, typeof(Credential));
                if (credential.Type != 1 || !credential.TargetName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!CredDelete(credential.TargetName, credential.Type, 0))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 1168) throw new Win32Exception(error);
                }
            }
        }
        finally { CredFree(entries); }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize; public IntPtr Blob; public uint Persist, AttributeCount;
        public IntPtr Attributes; public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredEnumerateW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr entries);
    [DllImport("advapi32.dll", EntryPoint="CredDeleteW", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr entries);
}
