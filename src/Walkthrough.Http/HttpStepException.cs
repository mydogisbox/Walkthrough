namespace Walkthrough.Http;

/// <summary>
/// Thrown when an HTTP workflow step fails, such as a non-success status code
/// or a missing base URL configuration.
/// </summary>
public class HttpStepException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
