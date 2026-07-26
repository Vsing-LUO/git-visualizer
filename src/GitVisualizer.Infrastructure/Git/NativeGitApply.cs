using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GitVisualizer.Infrastructure.Git;

internal sealed class NativeGitApply
{
    private const int ApplyLocationIndex = 1;
    private readonly nint library;
    private readonly RepositoryOpen repositoryOpen;
    private readonly RepositoryFree repositoryFree;
    private readonly DiffFromBuffer diffFromBuffer;
    private readonly DiffFree diffFree;
    private readonly ApplyOptionsInit applyOptionsInit;
    private readonly Apply apply;
    private readonly ErrorLast errorLast;

    public NativeGitApply()
    {
        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("不支持当前处理器架构。")
        };
        var runtimeDirectory = Path.Combine(
            AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native");
        var nativePath = (Directory.Exists(runtimeDirectory)
                ? Directory.EnumerateFiles(runtimeDirectory, "git2-*.dll")
                : [])
            .FirstOrDefault()
            ?? Directory.EnumerateFiles(
                    AppContext.BaseDirectory, "git2-*.dll", SearchOption.TopDirectoryOnly)
                .FirstOrDefault()
            ?? throw new DllNotFoundException("找不到 LibGit2Sharp 附带的 libgit2 原生库。");
        library = NativeLibrary.Load(nativePath);
        repositoryOpen = Load<RepositoryOpen>("git_repository_open");
        repositoryFree = Load<RepositoryFree>("git_repository_free");
        diffFromBuffer = Load<DiffFromBuffer>("git_diff_from_buffer");
        diffFree = Load<DiffFree>("git_diff_free");
        applyOptionsInit = Load<ApplyOptionsInit>("git_apply_options_init");
        apply = Load<Apply>("git_apply");
        errorLast = Load<ErrorLast>("git_error_last");
    }

    public void ApplyPatchToIndex(string repositoryPath, string patch)
    {
        var openResult = repositoryOpen(out var repository, repositoryPath);
        ThrowIfError(openResult, "无法打开仓库");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(patch);
            var buffer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var diffResult = diffFromBuffer(out var diff, buffer, (nuint)bytes.Length);
                ThrowIfError(diffResult, "无法解析差异块");
                try
                {
                    var options = new GitApplyOptions();
                    ThrowIfError(applyOptionsInit(ref options, 1), "无法初始化补丁选项");
                    ThrowIfError(apply(repository, diff, ApplyLocationIndex, ref options), "差异块无法应用到暂存区");
                }
                finally
                {
                    diffFree(diff);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            repositoryFree(repository);
        }
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private void ThrowIfError(int code, string prefix)
    {
        if (code >= 0)
        {
            return;
        }
        var pointer = errorLast();
        var error = pointer == nint.Zero
            ? default
            : Marshal.PtrToStructure<GitError>(pointer);
        var message = error.Message == nint.Zero
            ? new Win32Exception(code).Message
            : Marshal.PtrToStringUTF8(error.Message);
        throw new InvalidOperationException($"{prefix}：{message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GitApplyOptions
    {
        public uint Version;
        public nint DeltaCallback;
        public nint HunkCallback;
        public nint Payload;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GitError
    {
        public nint Message;
        public int ErrorClass;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RepositoryOpen(out nint repository, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RepositoryFree(nint repository);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DiffFromBuffer(out nint diff, nint content, nuint contentLength);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DiffFree(nint diff);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ApplyOptionsInit(ref GitApplyOptions options, uint version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Apply(nint repository, nint diff, int location, ref GitApplyOptions options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ErrorLast();
}
