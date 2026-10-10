namespace Communication.App.ViewModels;

public sealed record SendTargetOption(string Name, string Address);

public sealed record SendSourceOption(ConnectionViewModel? Endpoint, bool IsCurrent = false);
