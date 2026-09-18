using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.Diagnostics;

internal static class OperationResultLogging
{
    internal static async Task<GitOperationResult> PersistAsync(GitOperationResult result, Func<GitOperationResult, Task> persist)
    {
        try
        {
            await persist(result).ConfigureAwait(false);
            return result with { LogStatus = OperationLogStatus.Persisted };
        }
        catch (Exception error)
        {
            return result with { LogStatus = OperationLogStatus.Failed,
                Warnings = result.Warnings.Concat(new[] { "操作结果未改变；日志写入失败：" + error.Message }).ToArray() };
        }
    }
}
