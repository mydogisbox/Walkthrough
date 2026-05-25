using Walkthrough.Core;

namespace Walkthrough.Http;

/// <summary>
/// An execution target that sends requests over HTTP.
/// Register request types with their steps via Register&lt;TStep&gt;() before use.
/// </summary>
public class HttpTarget : Target<HttpTarget, HttpStep>, ITarget, IRawTarget
{
    protected readonly HttpExecutor Executor;
    private IReadOnlyDictionary<string, IFieldValue<string>> _headers = new Dictionary<string, IFieldValue<string>>();

    public HttpTarget(string baseUrl)
    {
        Executor = new HttpExecutor(baseUrl);
    }

    /// <summary>Sets the headers sent with every request.</summary>
    public HttpTarget WithHeaders(IReadOnlyDictionary<string, IFieldValue<string>> headers)
    {
        _headers = headers;
        return this;
    }

    Task<TResponse> ITarget.ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
    {
        var step          = GetStep(request);
        var targetHeaders = FieldValueResolver.ResolveGroup(_headers, context);
        return ((IHttpStep<TResponse>)step).RunAsync(Executor, resolvedFields, targetHeaders);
    }

    Task<object> IRawTarget.ExecuteRawAsync<TResponse>(WorkflowRequest<TResponse> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
    {
        var step          = GetStep(request);
        var targetHeaders = FieldValueResolver.ResolveGroup(_headers, context);
        return ((IHttpStep<TResponse>)step).RunRawAsync(Executor, resolvedFields, targetHeaders);
    }
}
