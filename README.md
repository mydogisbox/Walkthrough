# Walkthrough

Write API integration tests as multi-step workflows in C#.

A test should read as something a consumer of your system can do — log in, create a user, place an order — not as a sequence of HTTP calls. Walkthrough gives you request types and a runner for exactly that, with defaults covering everything a given test doesn't care about.

```csharp
public class NewUser_CanPlaceOrder : WalkthroughTestBase
{
    [Fact]
    public async Task Test()
    {
        await ExecuteAsync(new LoginRequest());
        await ExecuteAsync(new CreateUserRequest());
        await BuildAsync(new AddOrderItem());

        var order = await ExecuteAsync(new CreateOrderRequest());

        Assert.Equal("pending", order.Status);
    }
}
```

Every field a test names is a claim that the field matters to that test. Defaults are what keep that claim honest.

## Packages

| Package | Contents |
| --- | --- |
| `MCiccotti.Walkthrough.Core` | Request and response types, `WorkflowRunner`, `WorkflowContext`, field values |
| `MCiccotti.Walkthrough.Http` | `HttpTarget` and `HttpStep` — sends workflow requests over HTTP |
| `MCiccotti.Walkthrough.Json` | Run workflows defined in JSON rather than C# |

```bash
dotnet add package MCiccotti.Walkthrough.Http
```

`MCiccotti.Walkthrough.Http` depends on `MCiccotti.Walkthrough.Core`; `MCiccotti.Walkthrough.Json` depends on both. All target `net10.0`.

Package IDs carry the `MCiccotti.` prefix; the namespaces do not. Install `MCiccotti.Walkthrough.Http`, then `using Walkthrough.Http;`.

## Requests and steps

A request record describes the inputs to one step and the response it produces, with a default for every field:

```csharp
public record UserResponse(string Id, string Email, string FirstName, string Role);

public record CreateUserRequest() : WorkflowRequest<UserResponse>
{
    public IFieldValue<string> Email     { get; init; } = Generated(() => $"user-{Guid.NewGuid():N}@test.com");
    public IFieldValue<string> FirstName { get; init; } = Static("Test");
    public IFieldValue<string> Role      { get; init; } = Static("user");
}
```

A step binds that request to a transport:

```csharp
public class CreateUserStep : HttpStep<CreateUserRequest, UserResponse, CreateUserStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/users";
}
```

Path parameters are filled from `{placeholder}` segments automatically. Override `MapBody`, `MapQuery`, or `MapHeaders` when the wire shape differs from the record.

## Field values

```csharp
using static Walkthrough.Core.FieldValues;

Static("value")
Generated(() => Guid.NewGuid().ToString())
From(ctx => ctx.Get<UserResponse>("CreateUserRequest").Id)
```

`From` is how a later step reuses an earlier response. Referencing a prior value rather than hardcoding one keeps the test honest: it says *this depends on that*, not *this particular string matters*.

## Running a workflow

Register steps on a target and construct a runner. Targets dispatch by `CanHandle`, first match wins, so login and API calls can use different base URLs or carry different headers:

```csharp
var context = new WorkflowContext();

var loginTarget = new HttpTarget(BaseUrl)
    .Register<LoginStep>();

var apiTarget = new HttpTarget(BaseUrl)
    .Register<CreateUserStep>()
    .Register<CreateOrderStep>()
    .WithHeaders(new Dictionary<string, IFieldValue<string>>
    {
        ["Authorization"] = From(ctx =>
            $"Bearer {ctx.GetOrDefault<LoginResponse>("LoginRequest")?.Token ?? ""}")
    });

var runner = new WorkflowRunner(context, loginTarget, apiTarget);
```

Wrapping that in a test base class keeps the setup out of individual tests:

```csharp
protected Task<TResponse> ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request)
    => _runner.ExecuteAsync(request);

protected Task<TResponse> BuildAsync<TResponse>(BuildableRequest<TResponse> item)
    => _runner.BuildAsync(item);
```

Overriding a default uses a `with` expression:

```csharp
await BuildAsync(new AddOrderItem() with { ProductName = Static("Deluxe Widget"), Quantity = Static(3) });
```

`ExecuteRawAsync` returns the status code and body without throwing on a non-2xx response.

## Workflows in JSON

`MCiccotti.Walkthrough.Json` runs the same workflows from JSON definitions instead of C#, which is useful when the people writing workflows aren't the people compiling them. `JsonWorkflowRunner` executes a definition and returns a `WorkflowResult` carrying per-step requests, responses, assertion failures, and captures.

## Guidance for coding agents

`MCiccotti.Walkthrough.Core` ships its own usage and style guides inside the package. On build they are written into your project:

```
.claude/walkthrough/
├── claude.md          — library overview and testing philosophy
├── csharp-style.md    — C# API patterns and conventions
├── json-style.md      — JSON workflow definition format
└── upgrade-*.md       — version-to-version upgrade notes
```

To put them in front of an agent, import the entrypoint from your `CLAUDE.md`:

```
@import .claude/walkthrough/claude.md
```

The files are rewritten whenever the installed package version changes, so the guidance an agent reads always matches the version you actually have — including the upgrade notes, which is what makes a version bump something an agent can carry out on its own. Treat `.claude/walkthrough/` as generated — edits there are overwritten on the next build.

## License

MIT
