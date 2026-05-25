namespace Walkthrough.Core;

/// <summary>
/// Base class for execution targets. Manages step registration and routing by request type.
/// TSelf is the concrete target type (CRTP) — used only to preserve the return type of Register.
/// </summary>
public abstract class Target<TSelf, TStep>
    where TSelf : Target<TSelf, TStep>
    where TStep : IStep
{
    protected readonly Dictionary<string, TStep> _steps = new(StringComparer.OrdinalIgnoreCase);

    public virtual bool CanHandle(string key) => _steps.ContainsKey(key);

    public TSelf Register(TStep step)
    {
        _steps[step.RequestType.Name] = step;
        return (TSelf)this;
    }

    public TSelf Register<TConcreteStep>()
        where TConcreteStep : TStep, new()
    {
        return Register(new TConcreteStep());
    }

    protected TStep GetStep<TResponse>(WorkflowRequest<TResponse> request)
    {
        if (!_steps.TryGetValue(request.GetType().Name, out var step))
            throw new InvalidOperationException(
                $"No step registered for '{request.GetType().Name}'.");
        return step;
    }
}
