using System.Text.Json;
using Walkthrough.Http;

namespace Walkthrough.Json;

/// <summary>
/// Executes workflow steps over HTTP using <see cref="HttpExecutor"/>.
/// Constructed from a <see cref="TargetDefinition"/> (JSON config).
/// </summary>
public class JsonHttpTarget
{
    private readonly TargetDefinition _definition;

    public JsonHttpTarget(TargetDefinition definition) => _definition = definition;

    public bool CanHandle(string stepName) => _definition.Steps?.ContainsKey(stepName) == true;

    public IEnumerable<string> StepNames => _definition.Steps?.Keys ?? Enumerable.Empty<string>();

    public async Task<object?> ExecuteAsync(
        string stepName,
        Dictionary<string, object?> bodyFields,
        Dictionary<string, FieldValueDefinition>? pathParamOverrides,
        Dictionary<string, FieldValueDefinition>? queryOverrides,
        Dictionary<string, FieldValueDefinition>? headerOverrides,
        Dictionary<string, object?> captures)
    {
        var step = _definition.Steps![stepName];
        var (pathParams, queryParams, headers) = ResolveTransportParams(step, pathParamOverrides, queryOverrides, headerOverrides, captures);
        var method = new HttpMethod(step.Method.ToUpper());

        var responseJson = await HttpExecutor.SendAsync(
            _definition.BaseUrl, method, step.Path, pathParams, queryParams, bodyFields, headers);

        return ParseJsonResponse(responseJson);
    }

    public async Task<object?> ExecuteRawAsync(
        string stepName,
        Dictionary<string, object?> bodyFields,
        Dictionary<string, FieldValueDefinition>? pathParamOverrides,
        Dictionary<string, FieldValueDefinition>? queryOverrides,
        Dictionary<string, FieldValueDefinition>? headerOverrides,
        Dictionary<string, object?> captures)
    {
        var step = _definition.Steps![stepName];
        var (pathParams, queryParams, headers) = ResolveTransportParams(step, pathParamOverrides, queryOverrides, headerOverrides, captures);
        var method = new HttpMethod(step.Method.ToUpper());

        var (statusCode, rawBody) = await HttpExecutor.SendRawAsync(
            _definition.BaseUrl, method, step.Path, pathParams, queryParams, bodyFields, headers);

        object? parsedBody;
        try { parsedBody = ParseJsonResponse(rawBody); }
        catch { parsedBody = rawBody; }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = statusCode,
            ["body"] = parsedBody
        };
    }

    private (Dictionary<string, object?> PathParams, Dictionary<string, string> QueryParams, Dictionary<string, string> Headers)
        ResolveTransportParams(
            TargetStepDefinition step,
            Dictionary<string, FieldValueDefinition>? pathParamOverrides,
            Dictionary<string, FieldValueDefinition>? queryOverrides,
            Dictionary<string, FieldValueDefinition>? headerOverrides,
            Dictionary<string, object?> captures)
    {
        var pathParams = ResolveFieldGroup(step.PathParams, pathParamOverrides, captures);

        var rawQuery = ResolveFieldGroup(step.Query, queryOverrides, captures);
        var queryParams = rawQuery.ToDictionary(
            kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);

        var rawHeaders = ResolveFieldGroup(_definition.Headers, null, captures);
        foreach (var kv in ResolveFieldGroup(step.Headers, headerOverrides, captures))
            rawHeaders[kv.Key] = kv.Value;
        var headers = rawHeaders.ToDictionary(
            kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);

        return (pathParams, queryParams, headers);
    }

    private static Dictionary<string, object?> ResolveFieldGroup(
        Dictionary<string, FieldValueDefinition>? defs,
        Dictionary<string, FieldValueDefinition>? overrides,
        Dictionary<string, object?> captures)
    {
        var merged = new Dictionary<string, FieldValueDefinition>(StringComparer.OrdinalIgnoreCase);
        if (defs is not null)
            foreach (var (k, v) in defs) merged[k] = v;
        if (overrides is not null)
            foreach (var (k, v) in overrides) merged[k] = v;
        return merged.ToDictionary(
            kv => kv.Key,
            kv => JsonValueResolver.Resolve(kv.Value).Resolve(captures),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? ParseJsonResponse(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
                .Select(e => JsonValueResolver.JsonElementToObject(e))
                .ToList<object?>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseJson, HttpExecutor.DeserializeOptions);
    }
}
