using System.Reflection;

namespace AgenticWorkflows.Application;

public sealed record ControlPlaneStatus(string State, string SoftwareVersion);

public interface IControlPlaneStatusQuery
{
    ControlPlaneStatus Execute();
}

/// <summary>The application projection, independent of any presentation transport.</summary>
public sealed class ControlPlaneStatusQuery : IControlPlaneStatusQuery
{
    public ControlPlaneStatus Execute() => new("running",
        typeof(ControlPlaneStatusQuery).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
}
