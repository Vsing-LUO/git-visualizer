using System.Text.Json;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.Persistence;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        if (!File.Exists(LocalPaths.SettingsFile))
        {
            return AppSettings.Default;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(LocalPaths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                       .ConfigureAwait(false)
                   ?? AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = LocalPaths.SettingsFile + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, LocalPaths.SettingsFile, true);
        }
        finally
        {
            gate.Release();
        }
    }
}
