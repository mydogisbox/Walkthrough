namespace Walkthrough.Core;

/// <summary>
/// Represents an execution target — a combination of location and protocol.
/// Each target knows how to execute requests against a specific endpoint
/// using a specific transport (HTTP, gRPC, etc.).
/// </summary>
public interface ITarget
{
    Task<TResponse> ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request, Dictionary<string, object?> resolvedFields, WorkflowContext context);

    /// <summary>
    /// Returns true if this target can handle the given key.
    /// For C# workflows the key is the request type name; for JSON workflows it is the step name.
    /// </summary>
    bool CanHandle(string key);
}
