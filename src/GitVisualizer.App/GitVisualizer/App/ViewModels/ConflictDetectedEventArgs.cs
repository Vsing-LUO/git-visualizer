namespace GitVisualizer.App.ViewModels;

public sealed record ConflictDetectedEventArgs(int ConflictCount, string OperationName);
