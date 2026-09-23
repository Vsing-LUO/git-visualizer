// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using GitVisualizer.Core;

namespace GitVisualizer.Infrastructure.Persistence;

public sealed class SettingsStore : ISettingsStore
{
    private readonly LocalDataPaths dataPaths;

    public SettingsStore(LocalDataPaths? paths = null) => dataPaths = paths ?? LocalPaths.Default;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        dataPaths.EnsureCreated();
        if (!File.Exists(dataPaths.SettingsFile))
        {
            return AppSettings.Default;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(dataPaths.SettingsFile);
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
        dataPaths.EnsureCreated();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = dataPaths.SettingsFile + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, dataPaths.SettingsFile, true);
        }
        finally
        {
            gate.Release();
        }
    }
}
