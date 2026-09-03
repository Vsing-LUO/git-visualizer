using System;

namespace GitVisualizer.App.Dialogs;

public sealed record PushMonitorEntry(DateTimeOffset Timestamp, string Message);
