# C# style

_Current version: 0.5.2. Upgrading from 0.4.0? See [upgrade-0.4-to-0.5.md](upgrade-0.4-to-0.5.md). Upgrading from 0.3.0? See [upgrade-0.3-to-0.4.md](upgrade-0.3-to-0.4.md)._

---

## Field value factories

Three factories are available via `using static Walkthrough.Core.FieldValues`:

- `Static(value)` — returns the same value every time. The value is captured once at construction.
- `Generated(Func<T> factory)` — invokes the factory each time the field is resolved. Use for values that must be unique per resolution or per test run.
- `From(Func<WorkflowContext, T> selector)` — reads from the context at resolution time. Use for values that come from a prior step's captured response.

```csharp
public IFieldValue<string> Id      { get; init; } = Generated(() => Guid.NewGuid().ToString());
public IFieldValue<string> Email   { get; init; } = Generated(() => $"user-{Guid.NewGuid():N}@example.com");
public IFieldValue<string> Name    { get; init; } = Generators.RandomName(); // Generators returns Generated(...)
public IFieldValue<string> Token   { get; init; } = From(ctx => ctx.Get<LoginResponse>("login").Token);
public IFieldValue<string> BaseUrl { get; init; } = Static("http://localhost:5020");
```

`Generated` accepts any `Func<T>` — there is no built-in generator list in C#. That constraint only applies to the JSON `{ "generated": "guid" }` syntax.

`GetOrDefault` returns the captured response or `null` if the step hasn't run — useful in `From` lambdas where a prior step is optional:

```csharp
["Authorization"] = From(ctx => $"Bearer {ctx.GetOrDefault<LoginResponse>("login")?.Token ?? ""}")
```

---

## Field value rule

Properties on `WorkflowRequest` and `BuildableRequest` subclasses that can be overridden must be `IFieldValue<T>`. A raw `{ get; init; }` property bypasses resolution and cannot participate in the field value system:

```csharp
// Wrong — looks overridable but bypasses resolution
public string CommandType { get; init; } = "CreateTarget";

// Right
public IFieldValue<string> CommandType { get; init; } = Static("CreateTarget");
```

A raw property with no `init` is fine for fields that are intentionally fixed — a discriminator that should never change:

```csharp
// Fine — not overridable by design
public string CommandType { get; } = "CreateTarget";
```

---

## Request file layout

Response type, request record, and step class live in one file per API concept:

```csharp
// Order.cs
public record OrderResponse(string Id, string UserId, List<OrderItemResponse> Items, string Status);

public record AddOrderItemResponse(string ProductName, int Quantity, decimal UnitPrice);

public record AddOrderItem() : BuildableRequest<AddOrderItemResponse>
{
    public IFieldValue<string>  ProductName { get; init; } = Static("Widget");
    public IFieldValue<int>     Quantity    { get; init; } = Static(1);
    public IFieldValue<decimal> UnitPrice   { get; init; } = Static(9.99m);
}

public record CreateOrderRequest() : WorkflowRequest<OrderResponse, CreateOrderRequest>, IWorkflowRequest
{
    public static string StepName => "createOrder";
    public IFieldValue<string>       UserId { get; init; } = From(ctx => ctx.Get<UserResponse>("createUser").Id);
    public IFieldValue<List<object>> Items  { get; init; } = From(ctx => ctx.GetAccumulated<AddOrderItem>());
}

public class CreateOrderStep : HttpStep<CreateOrderRequest, OrderResponse, CreateOrderStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/orders";

    // MapBody is optional — the default passes all resolved fields through, excluding any whose
    // name matches a {placeholder} in Path. Override to rename, filter, or transform fields.
    public override Dictionary<string, object?> MapBody(Dictionary<string, object?> resolvedFields) => new()
    {
        ["UserId"] = resolvedFields["UserId"],
        ["Items"]  = resolvedFields["Items"],
    };
}
```

---

## URL parameters and query parameters

Path parameters are declared on the **request** as `IFieldValue<string>` fields. The step's `Path` contains `{placeholder}` segments — the step auto-extracts values by matching placeholder names to request field names (case-insensitive). Path param fields are automatically excluded from the request body.

```csharp
public record GetOrderRequest() : WorkflowRequest<OrderResponse, GetOrderRequest>, IWorkflowRequest
{
    public static string StepName => "getOrder";
    public IFieldValue<string> OrderId { get; init; } = From(ctx => ctx.Get<OrderResponse>("createOrder").Id);
}

public class GetOrderStep : HttpStep<GetOrderRequest, OrderResponse, GetOrderStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Get;
    public static string     Path   => "/orders/{orderId}";  // {orderId} matches OrderId field (case-insensitive)
}
```

Query parameters are declared via `MapQuery` on the step, which receives the resolved request fields and returns the query string key-value pairs:

```csharp
public record SearchOrdersRequest() : WorkflowRequest<List<OrderResponse>, SearchOrdersRequest>, IWorkflowRequest
{
    public static string StepName => "searchOrders";
    public IFieldValue<string> Status { get; init; } = Static("pending");
    public IFieldValue<string> UserId { get; init; } = From(ctx => ctx.Get<UserResponse>("createUser").Id);
}

public class SearchOrdersStep : HttpStep<SearchOrdersRequest, List<OrderResponse>, SearchOrdersStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Get;
    public static string     Path   => "/orders";

    public override Dictionary<string, string> MapQuery(Dictionary<string, object?> resolvedFields) => new()
    {
        ["status"] = resolvedFields["Status"]?.ToString() ?? "",
        ["userId"] = resolvedFields["UserId"]?.ToString() ?? "",
    };
}
```

Produces: `GET /orders?status=pending&userId=abc-123`

---

## Per-invocation overrides

Use the `with` expression to override request fields per call. This works for path params, query param sources, and any other request field:

```csharp
var first     = await ExecuteAsync(new CreateOrderRequest());
var second    = await ExecuteAsync(new CreateOrderRequest());
var retrieved = await ExecuteAsync(new GetOrderRequest() with { OrderId = Static(first.Id) });

var admins = await ExecuteAsync(new GetUsersByRoleRequest() with { Role = Static("admin") });
```

---

## Test class

xUnit creates a new instance per class — each test gets a fresh `WorkflowRunner` (and `WorkflowContext`) with no shared state:

```csharp
public class PlacedOrder_CanBeRetrieved : WalkthroughTestBase
{
    [Fact]
    public async Task Test()
    {
        await ExecuteAsync(new LoginRequest());
        await ExecuteAsync(new CreateUserRequest());
        await BuildAsync(new AddOrderItem());
        var created   = await ExecuteAsync(new CreateOrderRequest());
        var retrieved = await ExecuteAsync(new GetOrderRequest());

        Assert.Equal(created.Id, retrieved.Id);
        Assert.Equal("pending", retrieved.Status);
    }
}
```

---

## Polling

`PollAsync` re-executes a step on an interval until a predicate passes or the timeout is reached. The final response is captured under the step name and returned:

```csharp
var order = await runner.PollAsync(
    new GetOrderRequest(),
    r => r.Status == "shipped",
    intervalMs: 500,
    timeoutMs: 10000);

Assert.Equal("shipped", order.Status);
```

- `intervalMs` — delay between attempts (default: 500)
- `timeoutMs` — max wait before throwing `WorkflowContextException` (default: 10000)
- Only the final passing response is captured; intermediate attempts overwrite each other

Use `PollAsync` directly on `WorkflowRunner`, not through the `WalkthroughTestBase` helpers:

```csharp
var runner = new WorkflowRunner(new WorkflowContext(), stepName => ...);
await runner.ExecuteAsync(new LoginRequest());
await runner.ExecuteAsync(new CreateOrderRequest());

var order = await runner.PollAsync(
    new GetOrderRequest(),
    r => r.Status == "shipped");
```

---

## Raw execution

`ExecuteRawAsync` sends the request without throwing on non-2xx responses and returns `object`. The concrete type depends on the target — `HttpTarget` returns `HttpRawResult`, which carries `StatusCode` and `Body`. Cast accordingly at the call site:

```csharp
await ExecuteAsync(new LoginRequest());
await ExecuteAsync(new CreateUserRequest());
await BuildAsync(new AddOrderItem());

var raw   = (HttpRawResult)await ExecuteRawAsync(new CreateOrderRequest());
var order = (OrderResponse)raw.Body!;

Assert.Equal(201, raw.StatusCode);
Assert.Equal("pending", order.Status);
```

On non-2xx responses, `Body` is the deserialized `TResponse` when the body matches that shape — otherwise the raw JSON string:

```csharp
var raw = (HttpRawResult)await ExecuteRawAsync(
    new CreateOrderRequest() with { UserId = Static("nonexistent-id") });

Assert.Equal(400, raw.StatusCode);
```

`HttpTarget` implements `IRawTarget` automatically for all registered steps — no additional setup required. Custom targets that implement `IRawTarget` return whatever type makes sense for their transport.

---

## Custom targets and multi-target routing

`WorkflowRunner` is target-agnostic — it routes each step to whatever `ITarget` the resolver returns, then captures the response. `HttpTarget` is one implementation; any class can implement `ITarget` to wrap an SDK, a raw `HttpClient` call, or an in-memory stub.

All captures are shared through the same `WorkflowContext` regardless of which target produced them. A `From` lambda can read captures from any prior step, even one that ran against a different target.

`WorkflowRunner` accepts multiple targets directly. Each request is routed to the first target whose `CanHandle` returns true. `HttpTarget.CanHandle` returns true only for request types it has a registered step for, so targets act as precise overrides with the last target serving as the catch-all:

```csharp
var loginTarget = new HttpTarget(SampleApiUrl)
    .Register<LoginStep>();

var apiTarget = new HttpTarget(SampleApiUrl)
    .Register<CreateUserStep>()
    .Register<CreateOrderStep>()
    .WithHeaders(new Dictionary<string, IFieldValue<string>>
    {
        ["Authorization"] = From(ctx => $"Bearer {ctx.Get<LoginResponse>("login").Token}")
    });

var runner = new WorkflowRunner(new WorkflowContext(), loginTarget, apiTarget);
```

`LoginRequest` is handled by `loginTarget`; everything else falls through to `apiTarget`.

### Custom ITarget

Any class can implement `ITarget`. `CanHandle` controls routing — return true for all types a catch-all handles, or check specific types for a targeted override:

```csharp
private class DirectGetOrderTarget(string baseUrl) : ITarget
{
    private static readonly HttpClient _http = new();

    public bool CanHandle(Type requestType) => requestType == typeof(GetOrderRequest);

    public async Task<TResponse> ExecuteAsync<TResponse>(
        WorkflowRequest<TResponse> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
    {
        var orderId = resolvedFields["OrderId"]?.ToString();
        var token   = context.Get<LoginResponse>("login").Token;

        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/orders/{orderId}");
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        var response = await _http.SendAsync(req);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TResponse>(json, _readOptions)!;
    }
}

var runner = new WorkflowRunner(new WorkflowContext(),
    new DirectGetOrderTarget(SampleApiUrl), authTarget, apiTarget);
```

For cases where type-based routing isn't enough, `WorkflowRunner` also accepts a `Func<string, ITarget>` resolver that maps step names directly.

### Dispatching through a registered HttpStep

If a custom target wraps `HttpTarget`-style dispatch rather than using a raw `HttpClient`, cast the stored step to `IHttpStep<TResponse>` to call `RunAsync` or `RunRawAsync` directly:

```csharp
var step = /* retrieved HttpStep instance */;
var result = await ((IHttpStep<TResponse>)step).RunAsync(baseUrl, resolvedFields, targetHeaders);
```

`HttpStep` can only be extended through `HttpStep<TRequest, TResponse, TSelf>` — direct subclassing of `HttpStep` is prevented at compile time. Any registered step is guaranteed to implement `IHttpStep<TResponse>` for its declared response type.

---

## Workflows as functions

A workflow can be extracted into a plain function that takes `Func<string, ITarget>` and constructs its own runner. The same function runs unchanged against any combination of targets:

```csharp
private static async Task<OrderResponse> PlaceOrder(Func<string, ITarget> resolver)
{
    var runner = new WorkflowRunner(new WorkflowContext(), resolver);
    await runner.ExecuteAsync(new LoginRequest());
    await runner.ExecuteAsync(new CreateUserRequest());
    await runner.BuildAsync(new AddOrderItem());
    return await runner.ExecuteAsync(new CreateOrderRequest());
}

await PlaceOrder(stepName => stepName == "login" ? (ITarget)authTarget : apiTarget);

await PlaceOrder(stepName => stepName == "login"
    ? (ITarget)new DirectLoginTarget(SampleApiUrl)
    : apiTarget);
```

---

## Field value resolution

`FieldValueResolver` resolves `IFieldValue<T>` properties on any request or build item. Resolution is recursive: after resolving `IFieldValue<T>` → `T`, if `T` is itself a record with `IFieldValue<U>` properties, those are resolved too — producing a nested `Dictionary<string, object?>`. List elements are also recursed into.

```csharp
public record RegionFields
{
    public IFieldValue<string> State   { get; init; } = Static("IL");
    public IFieldValue<string> Country { get; init; } = Static("US");
}

public record AddressFields
{
    public IFieldValue<string>       Street { get; init; } = Static("123 Main St");
    public IFieldValue<string>       City   { get; init; } = Static("Springfield");
    public IFieldValue<RegionFields> Region { get; init; } = Static(new RegionFields());
}

public record UpdateUserAddressRequest() : WorkflowRequest<UpdateUserAddressResponse, UpdateUserAddressRequest>, IWorkflowRequest
{
    public static string StepName => "updateUserAddress";
    public IFieldValue<string>        UserId  { get; init; } = From(ctx => ctx.Get<UserResponse>("createUser").Id);
    public IFieldValue<AddressFields> Address { get; init; } = Static(new AddressFields());
}

public class UpdateUserAddressStep : HttpStep<UpdateUserAddressRequest, UpdateUserAddressResponse, UpdateUserAddressStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Put;
    public static string     Path   => "/users/{userId}/address";
}
```

Overriding only what differs — unspecified fields keep their defaults:

```csharp
var result = await ExecuteAsync(
    new UpdateUserAddressRequest() with
    {
        Address = Static(new AddressFields
        {
            City   = Static("Boston"),
            Region = Static(new RegionFields { State = Static("MA") })
        })
    });

Assert.Equal("Boston",      result.Address.City);
Assert.Equal("123 Main St", result.Address.Street);  // default preserved
Assert.Equal("MA",          result.Address.Region.State);
Assert.Equal("US",          result.Address.Region.Country);  // default preserved
```

---

## Building an array

`BuildAsync` resolves all `IFieldValue<T>` properties immediately, stores the typed `TResponse` in the accumulation, and returns that same snapshot. `GetAccumulated<TItem>()` returns `List<object>` — each element is the concrete `TResponse` produced by `BuildAsync` — and **clears the accumulation**. Resolution happens once at build time, not again when the request is sent.

`ResolveRecursively` boxes lists as `List<object?>`. In `MapBody`, cast accumulated fields accordingly:

```csharp
public override Dictionary<string, object?> MapBody(Dictionary<string, object?> resolvedFields) => new()
{
    ["Commands"] = (List<object?>)resolvedFields["Commands"],
};
```

`BuildAsync` returns `TResponse` — a plain record with resolved values, not `IFieldValue<T>` wrappers:

```csharp
var widget = await BuildAsync(new AddOrderItem() with { ProductName = Static("Deluxe Widget"), Quantity = Static(3) });
await BuildAsync(new AddOrderItem() with { ProductName = Static("Basic Widget") });
var order = await ExecuteAsync(new CreateOrderRequest());

Assert.Equal("Deluxe Widget", widget.ProductName);
Assert.Equal(3, widget.Quantity);
```

---

## Accumulating multiple variants

When all variants share the same fields, use static factory methods:

```csharp
public record AddOrderItem() : BuildableRequest<AddOrderItemResponse>
{
    public IFieldValue<string>  ProductName { get; init; } = Static("Widget");
    public IFieldValue<int>     Quantity    { get; init; } = Static(1);
    public IFieldValue<decimal> UnitPrice   { get; init; } = Static(9.99m);

    public static AddOrderItem Deluxe() => new() with { ProductName = Static("Deluxe Widget"), UnitPrice = Static(49.99m) };
    public static AddOrderItem Basic()  => new() with { ProductName = Static("Basic Widget") };
}
```

When variants have genuinely different fields, use subtypes and override `AccumulationKey` on the base:

```csharp
public abstract record OrderItem() : BuildableRequest<OrderItemResponse>
{
    public override Type AccumulationKey => typeof(OrderItem);
}

public record PhysicalItem() : OrderItem
{
    public IFieldValue<string> ProductName     { get; init; } = Static("Widget");
    public IFieldValue<string> ShippingAddress { get; init; } = Static("123 Main St");
}

public record DigitalItem() : OrderItem
{
    public IFieldValue<string> ProductName { get; init; } = Static("E-Book");
    public IFieldValue<string> DownloadUrl { get; init; } = Static("https://example.com/download");
}
```

`GetAccumulated<OrderItem>()` retrieves both `PhysicalItem` and `DigitalItem` builds.

---

## Headers

Override `MapHeaders` on `HttpStep` for step-level headers. It receives the resolved request fields and returns headers to add or override:

```csharp
public class CreateItemStep : HttpStep<CreateItemRequest, ItemResponse, CreateItemStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/items";
    public override Dictionary<string, string> MapHeaders(Dictionary<string, object?> resolvedFields)
        => new() { ["X-Api-Version"] = "2" };
}
```

To drive a header from a request field, add an `IFieldValue<string>` to the request and read it in `MapHeaders`:

```csharp
public record CreateItemRequest() : WorkflowRequest<ItemResponse, CreateItemRequest>, IWorkflowRequest
{
    public static string StepName => "createItem";
    public IFieldValue<string> RequestId { get; init; } = Generated(() => Guid.NewGuid().ToString());
}

public class CreateItemStep : HttpStep<CreateItemRequest, ItemResponse, CreateItemStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/items";
    public override Dictionary<string, string> MapHeaders(Dictionary<string, object?> resolvedFields)
        => new() { ["X-Request-Id"] = resolvedFields["RequestId"]?.ToString() ?? "" };
}
```

Supply target-level headers via `WithHeaders`. Use `From` for headers that depend on a prior step's response:

```csharp
new HttpTarget(SampleApiUrl)
    .Register<CreateItemStep>()
    .WithHeaders(new Dictionary<string, IFieldValue<string>>
    {
        ["X-Tenant-Id"]   = Static("acme"),
        ["Authorization"] = From(ctx => $"Bearer {ctx.Get<LoginResponse>("login").Token}")
    })
```
