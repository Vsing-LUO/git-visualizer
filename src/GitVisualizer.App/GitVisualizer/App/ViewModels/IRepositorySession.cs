namespace GitVisualizer.App.ViewModels;

internal interface IRepositorySession : IDisposable
{
    string RepositoryPath { get; }
    void Switch(string path);
    RequestContext Begin(string channel);
    RequestContext Capture();
    void Invalidate(string channel);
    bool Current(RequestContext request);
}
