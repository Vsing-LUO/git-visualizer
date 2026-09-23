// Copyright 2026 赵泽璇
// SPDX-License-Identifier: Apache-2.0

namespace GitVisualizer.App.ViewModels;

// UI-thread owned. Every result carries both repository identity and a per-query version.
internal sealed class RepositoryRequests : IRepositorySession
{
    private readonly Dictionary<string, RequestContext> active = new();
    private readonly List<CancellationTokenSource> sources = new();
    private CancellationTokenSource sessionCancellation = new();
    private long session;
    private long version;
    private bool disposed;
    public string RepositoryPath { get; private set; } = string.Empty;

    public void Switch(string path)
    {
        if (disposed) return;
        sessionCancellation.Cancel();
        sources.Add(sessionCancellation);
        foreach (var request in active.Values) request.Cancel();
        active.Clear();
        sessionCancellation = new CancellationTokenSource();
        RepositoryPath = path;
        session++;
    }

    public RequestContext Begin(string channel)
    {
        if (disposed) return new RequestContext(this, session, RepositoryPath, channel, ++version, new CancellationToken(true), null);
        if (active.TryGetValue(channel, out var previous)) previous.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
        var request = new RequestContext(this, session, RepositoryPath, channel, ++version, cancellation.Token, cancellation);
        active[channel] = request;
        return request;
    }

    public RequestContext Capture() => new(this, session, RepositoryPath, null, version, disposed ? new CancellationToken(true) : sessionCancellation.Token, null);
    public void Invalidate(string channel) { if (active.Remove(channel, out var request)) request.Cancel(); }
    public bool Current(RequestContext request) => !disposed && request.Session == session && !request.Token.IsCancellationRequested &&
        (request.Channel is null || (active.TryGetValue(request.Channel, out var latest) && ReferenceEquals(request, latest)));
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sessionCancellation.Cancel();
        foreach (var request in active.Values) request.Cancel();
        // Running requests own their linked sources; their finally blocks may still execute.
        foreach (var source in sources) source.Dispose();
        sessionCancellation.Dispose();
    }
}

internal sealed class RequestContext(IRepositorySession owner, long session, string repositoryPath,
    string? channel, long version, CancellationToken token, CancellationTokenSource? cancellation) : IDisposable
{
    internal long Session { get; } = session;
    internal long Version { get; } = version;
    public string RepositoryPath { get; } = repositoryPath;
    internal string? Channel { get; } = channel;
    internal CancellationToken Token { get; } = token;
    internal Func<bool>? AdditionalValidity { get; set; }
    internal bool IsCurrent => owner.Current(this) && (AdditionalValidity?.Invoke() ?? true);
    private bool disposed;
    internal void Cancel() { if (!disposed) cancellation?.Cancel(); }
    internal void Check() { if (!IsCurrent) throw new OperationCanceledException("请求已过期。", Token); }
    internal async Task<T> Await<T>(Task<T> task) { var result = await task; Check(); return result; }
    internal async Task Await(Task task) { await task; Check(); }
    public void Dispose() { disposed = true; cancellation?.Dispose(); }
}
