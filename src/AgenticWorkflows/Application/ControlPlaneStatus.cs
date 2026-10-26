using System.Reflection;

namespace AgenticWorkflows.Application;

public sealed record DatabaseReadiness(string State, string Path, int SupportedSchema, long? ActiveSchema,
    bool DatabaseReady, bool WorkflowReady, string Message, string? Guidance);
public sealed record ControlPlaneStatus(string State, string SoftwareVersion, DatabaseReadiness? Database = null);

public interface IControlPlaneStatusQuery
{
    ControlPlaneStatus Execute();
}

/// <summary>The application projection, independent of any presentation transport.</summary>
public sealed class ControlPlaneStatusQuery(Func<DatabaseReadiness>? database = null) : IControlPlaneStatusQuery
{
    public ControlPlaneStatus Execute() => new("running",
        typeof(ControlPlaneStatusQuery).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, database?.Invoke());
}
