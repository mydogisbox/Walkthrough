using System.Text.Json;

namespace Walkthrough.Core;

/// <summary>
/// Executes workflow steps against targets, handles polling, and accumulates build items.
/// Each runner holds a WorkflowContext for shared state and a resolver that maps step names to targets.
/// </summary>
public class WorkflowRunner
{
    private readonly WorkflowContext _context;
    private readonly Func<string, ITarget>? _resolver;

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>Routes each request to the first target whose CanHandle returns true.</summary>
    public WorkflowRunner(WorkflowContext context, params ITarget[] targets)
    {
        _context = context;
        _resolver = key => Array.Find(targets, t => t.CanHandle(key))
            ?? throw new WorkflowContextException($"No target can handle '{key}'.");
    }

    /// <summary>Routes each step to the target returned by the resolver.</summary>
    public WorkflowRunner(WorkflowContext context, Func<string, ITarget> resolver)
    {
        _context  = context;
        _resolver = resolver;
    }

    /// <summary>Build-only runner — ExecuteAsync will throw if called.</summary>
    public WorkflowRunner(WorkflowContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Executes a request against the resolved target, captures the response, and returns it.
    /// </summary>
    public async Task<TResponse> ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request)
    {
        var key            = request.GetType().Name;
        var target         = Resolve(key);
        var resolvedFields = FieldValueResolver.Resolve(request, _context);
        var response       = await target.ExecuteAsync(request, resolvedFields, _context);
        _context.CaptureRaw(key, response!);
        return response;
    }

    /// <summary>
    /// Executes a request without throwing on failure, captures the result, and returns it as object.
    /// The caller casts to the expected type. Requires the target to implement IRawTarget.
    /// </summary>
    public async Task<object> ExecuteRawAsync<TResponse>(WorkflowRequest<TResponse> request)
    {
        var key    = request.GetType().Name;
        var target = Resolve(key);
        if (target is not IRawTarget rawTarget)
            throw new WorkflowContextException(
                $"Target for '{key}' does not implement IRawTarget.");

        var resolvedFields = FieldValueResolver.Resolve(request, _context);
        var result         = await rawTarget.ExecuteRawAsync(request, resolvedFields, _context);
        _context.CaptureRaw(key, result);
        return result;
    }

    private ITarget Resolve(string key)
    {
        if (_resolver is not null)
            return _resolver(key);

        throw new WorkflowContextException(
            "No target resolver registered. Provide targets or a resolver when constructing WorkflowRunner.");
    }

    /// <summary>
    /// Repeatedly executes a request until <paramref name="until"/> returns true
    /// or <paramref name="timeoutMs"/> elapses, with <paramref name="intervalMs"/> between attempts.
    /// </summary>
    public async Task<TResponse> PollAsync<TResponse>(
        WorkflowRequest<TResponse> request,
        Func<TResponse, bool> until,
        int intervalMs = 500,
        int timeoutMs  = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var response = await ExecuteAsync(request);
            if (until(response)) return response;

            var remaining = timeoutMs - sw.ElapsedMilliseconds;

            if (remaining <= 0)
                throw new WorkflowContextException(
                    $"PollAsync timed out after {timeoutMs}ms waiting for '{request.GetType().Name}'.");

            await Task.Delay((int)Math.Min(intervalMs, remaining));
        }
    }

    /// <summary>
    /// Resolves all field values on the build item, appends the resolved dictionary to the
    /// accumulation, captures the individual result, and returns a typed snapshot.
    /// </summary>
    public Task<TResponse> BuildAsync<TResponse>(BuildableRequest<TResponse> item)
    {
        var resolved = FieldValueResolver.ResolveObject(item, _context);
        var json     = JsonSerializer.Serialize(resolved);
        var response = JsonSerializer.Deserialize<TResponse>(json, _jsonOptions)!;
        _context.Accumulate(item.AccumulationKey, response!);
        _context.CaptureRaw(item.BuildableName, response!);
        return Task.FromResult(response);
    }
}
