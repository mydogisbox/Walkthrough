using Walkthrough.Core;
using Walkthrough.Http;
using static Walkthrough.Core.FieldValues;

namespace Walkthrough.SampleWorkflows;

public record LoginResponse(string Token, string UserId);

public record LoginRequest() : WorkflowRequest<LoginResponse>
{
    public IFieldValue<string> Username { get; init; } = Generated(() => $"user-{Guid.NewGuid():N}@test.com");
    public IFieldValue<string> Password { get; init; } = Static("Password123!");
}

public class LoginStep : HttpStep<LoginRequest, LoginResponse, LoginStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/auth/login";
}
