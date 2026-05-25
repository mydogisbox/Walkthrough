using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Walkthrough.Http;

public record HttpSendResult(bool IsSuccess, int StatusCode, string Body, bool IsTransient);

/// <summary>
/// HTTP transport bound to a base URL. Handles request construction, sending, and response reading.
/// Static helpers (Deserialize, serialization options) remain available without an instance.
/// </summary>
public class HttpExecutor
{
    public static readonly JsonSerializerOptions SerializeOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HttpClient SharedClient =
        new(new HttpClientHandler { AllowAutoRedirect = false });

    private readonly string _baseUrl;

    public HttpExecutor(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    public async Task<HttpSendResult> TrySendAsync(
        HttpMethod method,
        string path,
        Dictionary<string, object?> pathParams,
        Dictionary<string, string> queryParams,
        Dictionary<string, object?> bodyFields,
        Dictionary<string, string> headers)
    {
        try
        {
            var response = await SendCoreAsync(method, path, pathParams, queryParams, bodyFields, headers);
            var body = await response.Content.ReadAsStringAsync();
            var statusCode = (int)response.StatusCode;
            var isTransient = statusCode is 503 or 504 or 429 or 404;
            return new HttpSendResult(response.IsSuccessStatusCode, statusCode, body, isTransient);
        }
        catch (HttpRequestException ex)
        {
            return new HttpSendResult(false, 0, ex.Message, IsTransient: true);
        }
    }

    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        Dictionary<string, object?> pathParams,
        Dictionary<string, string> queryParams,
        Dictionary<string, object?> bodyFields,
        Dictionary<string, string> headers)
    {
        var result = await TrySendAsync(method, path, pathParams, queryParams, bodyFields, headers);

        if (!result.IsSuccess)
        {
            if (result.StatusCode == 0)
                throw new HttpRequestException(result.Body);
            throw new HttpStepException(
                $"HTTP {method} {_baseUrl}/{path.TrimStart('/')} failed with {result.StatusCode}. Body: {result.Body}",
                result.StatusCode);
        }

        return result.Body;
    }

    public async Task<(int StatusCode, string Body)> SendRawAsync(
        HttpMethod method,
        string path,
        Dictionary<string, object?> pathParams,
        Dictionary<string, string> queryParams,
        Dictionary<string, object?> bodyFields,
        Dictionary<string, string> headers)
    {
        var result = await TrySendAsync(method, path, pathParams, queryParams, bodyFields, headers);
        return (result.StatusCode, result.Body);
    }

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, DeserializeOptions)
            ?? throw new HttpStepException($"Response deserialized to null for type '{typeof(T).Name}'.");

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string path,
        Dictionary<string, object?> pathParams,
        Dictionary<string, string> queryParams,
        Dictionary<string, object?> bodyFields,
        Dictionary<string, string> headers)
    {
        foreach (var (key, value) in pathParams)
            path = path.Replace($"{{{key}}}", Uri.EscapeDataString(value?.ToString() ?? ""),
                StringComparison.OrdinalIgnoreCase);

        if (queryParams.Count > 0)
        {
            var qs = string.Join("&", queryParams.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            path = path.TrimEnd('?', '&') + (path.Contains('?') ? "&" : "?") + qs;
        }

        var url = _baseUrl + "/" + path.TrimStart('/');
        var httpRequest = new HttpRequestMessage(method, url);

        if (method != HttpMethod.Get && method != HttpMethod.Delete && bodyFields.Count > 0)
        {
            var json = JsonSerializer.Serialize(bodyFields, SerializeOptions);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        foreach (var (key, value) in headers)
            httpRequest.Headers.TryAddWithoutValidation(key, value);

        return await SharedClient.SendAsync(httpRequest);
    }
}
