namespace Walkthrough.Core;

/// <summary>
/// Pure state bag shared across all steps in a workflow execution.
/// Stores step responses and build-step accumulations.
/// Execution is handled by a runner such as WorkflowRunner.
/// </summary>
public class WorkflowContext
{
    private readonly Dictionary<string, object> _captures = new();
    private readonly Dictionary<Type, List<object>> _accumulated = new();

    /// <summary>
    /// Appends a typed build result to the accumulation list for the given key.
    /// Called by runners during BuildAsync.
    /// </summary>
    public void Accumulate(Type key, object response)
    {
        if (!_accumulated.TryGetValue(key, out var list))
        {
            list = [];
            _accumulated[key] = list;
        }
        list.Add(response);
    }

    /// <summary>
    /// Returns all accumulated build results for the given item type as typed objects,
    /// then clears the accumulation. Returns an empty list if nothing has been accumulated.
    /// </summary>
    public List<object> GetAccumulated<TItem>() where TItem : BuildableRequest
    {
        if (!_accumulated.TryGetValue(typeof(TItem), out var list))
            return [];

        _accumulated.Remove(typeof(TItem));
        return list;
    }

    /// <summary>
    /// Retrieves the captured response of a previous step by step name.
    /// </summary>
    public T Get<T>(string stepName)
    {
        if (!_captures.TryGetValue(stepName, out var value))
            throw new WorkflowContextException(
                $"No captured response found for step '{stepName}'. " +
                $"Ensure the step has been executed before referencing its output. " +
                $"Available steps: [{string.Join(", ", _captures.Keys)}]");

        if (value is not T typed)
            throw new WorkflowContextException(
                $"Captured response for step '{stepName}' is of type '{value.GetType().Name}', " +
                $"not '{typeof(T).Name}'.");

        return typed;
    }

    /// <summary>
    /// Returns the captured response for the given step name, or null if the step has not run.
    /// </summary>
    public T? GetOrDefault<T>(string stepName) where T : class
    {
        if (_captures.TryGetValue(stepName, out var value) && value is T typed)
            return typed;
        return null;
    }

    /// <summary>
    /// Returns the raw captured object for a step without type casting.
    /// Used by JSON workflow runners where the response type is not known at compile time.
    /// </summary>
    public object? GetRaw(string stepName)
    {
        if (!_captures.TryGetValue(stepName, out var value))
            throw new WorkflowContextException(
                $"No captured response found for step '{stepName}'. " +
                $"Available steps: [{string.Join(", ", _captures.Keys)}]");
        return value;
    }

    /// <summary>
    /// Captures a raw response under the given step name.
    /// Used by runners and JSON workflow runners.
    /// </summary>
    public void CaptureRaw(string stepName, object response)
    {
        _captures[stepName] = response;
    }
}
