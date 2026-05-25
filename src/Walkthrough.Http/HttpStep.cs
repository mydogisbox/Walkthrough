using System.Text.RegularExpressions;
using Walkthrough.Core;

namespace Walkthrough.Http;

public interface IHttpStep
{
    static abstract HttpMethod Method { get; }
    static abstract string     Path   { get; }
}

public interface IHttpStep<TResponse>
{
    Task<TResponse> RunAsync(HttpExecutor executor, Dictionary<string, object?> resolvedFields, Dictionary<string, string> targetHeaders);
    Task<object>   RunRawAsync(HttpExecutor executor, Dictionary<string, object?> resolvedFields, Dictionary<string, string> targetHeaders);
}

public abstract class HttpStep : IStep
{
    internal HttpStep() { }

    protected static readonly Regex PlaceholderRegex =
        new(@"\{(\w+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public abstract Type RequestType { get; }

    public virtual Dictionary<string, object?> MapBody(Dictionary<string, object?> resolvedFields)    => resolvedFields;
    public virtual Dictionary<string, string>  MapQuery(Dictionary<string, object?> resolvedFields)   => [];
    public virtual Dictionary<string, string>  MapHeaders(Dictionary<string, object?> resolvedFields) => [];
}

public abstract class HttpStep<TRequest, TResponse, TSelf> : HttpStep, IHttpStep<TResponse>
    where TRequest : WorkflowRequest<TResponse>
    where TSelf    : HttpStep<TRequest, TResponse, TSelf>, IHttpStep
{
    public override Type RequestType => typeof(TRequest);

    async Task<TResponse> IHttpStep<TResponse>.RunAsync(
        HttpExecutor executor,
        Dictionary<string, object?> resolvedFields,
        Dictionary<string, string> targetHeaders)
    {
        var (pathParams, query, headers, body) = PrepareRequest(resolvedFields, targetHeaders);
        var json = await executor.SendAsync(TSelf.Method, TSelf.Path, pathParams, query, body, headers);
        return HttpExecutor.Deserialize<TResponse>(json);
    }

    async Task<object> IHttpStep<TResponse>.RunRawAsync(
        HttpExecutor executor,
        Dictionary<string, object?> resolvedFields,
        Dictionary<string, string> targetHeaders)
    {
        var (pathParams, query, headers, body) = PrepareRequest(resolvedFields, targetHeaders);
        var (statusCode, json) = await executor.SendRawAsync(TSelf.Method, TSelf.Path, pathParams, query, body, headers);
        object? responseBody;
        try   { responseBody = string.IsNullOrEmpty(json) ? null : HttpExecutor.Deserialize<TResponse>(json); }
        catch { responseBody = json; }
        return new HttpRawResult(statusCode, responseBody);
    }

    private (Dictionary<string, object?> pathParams,
             Dictionary<string, string>   query,
             Dictionary<string, string>   headers,
             Dictionary<string, object?>  body) PrepareRequest(
        Dictionary<string, object?> resolvedFields,
        Dictionary<string, string>  targetHeaders)
    {
        var pathParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathParams     = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PlaceholderRegex.Matches(TSelf.Path))
        {
            var name  = match.Groups[1].Value;
            pathParamNames.Add(name);
            var field = resolvedFields.Keys.FirstOrDefault(k =>
                string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (field is not null)
                pathParams[name] = resolvedFields[field];
        }

        var query   = MapQuery(resolvedFields);
        var headers = new Dictionary<string, string>(targetHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in MapHeaders(resolvedFields))
            headers[kv.Key] = kv.Value;
        var body = MapBody(resolvedFields)
            .Where(kv => !pathParamNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        return (pathParams, query, headers, body);
    }
}
