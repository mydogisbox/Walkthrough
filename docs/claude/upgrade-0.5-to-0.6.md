# Upgrading from 0.5.x to 0.6.0

## IWorkflowRequest and StepName removed

Request types no longer implement `IWorkflowRequest` or declare `StepName`. The CRTP base `WorkflowRequest<TResponse, TSelf>` is also removed.

```csharp
// Before
public record LoginRequest() : WorkflowRequest<LoginResponse, LoginRequest>, IWorkflowRequest
{
    public static string StepName => "login";
}

// After
public record LoginRequest() : WorkflowRequest<LoginResponse>;
```

## Captures keyed by type name

Responses are now captured under `request.GetType().Name` instead of `StepName`. Update all `Get`, `GetOrDefault`, and `From` references:

```csharp
// Before
ctx.Get<LoginResponse>("login")
ctx.Get<UserResponse>("createUser")
From(ctx => ctx.Get<OrderResponse>("createOrder").Id)

// After
ctx.Get<LoginResponse>(nameof(LoginRequest))
ctx.Get<UserResponse>(nameof(CreateUserRequest))
From(ctx => ctx.Get<OrderResponse>(nameof(CreateOrderRequest)).Id)
```

Using `nameof` gives compile-time safety — if the request type is renamed, references update automatically.

## ExecuteAsync simplified

`ExecuteAsync` and `PollAsync` no longer require the `TSelf` type parameter:

```csharp
// Before
public Task<TResponse> ExecuteAsync<TResponse, TSelf>(WorkflowRequest<TResponse, TSelf> request)
    where TSelf : WorkflowRequest<TResponse, TSelf>, IWorkflowRequest

// After
public Task<TResponse> ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request)
```

Call sites are unchanged — type inference still works.

## CanHandle takes string

`ITarget.CanHandle` now takes a `string` key instead of `Type`. For C# workflows, the key is the request type name. For JSON workflows, the key is the step name from the workflow definition.

```csharp
// Before
public bool CanHandle(Type requestType) => requestType == typeof(GetOrderRequest);

// After
public bool CanHandle(string key) => key == nameof(GetOrderRequest);
```

## Resolver key changed

The `Func<string, ITarget>` resolver now receives the request type name instead of the step name:

```csharp
// Before
var runner = new WorkflowRunner(context, stepName => stepName == "login" ? authTarget : apiTarget);

// After
var runner = new WorkflowRunner(context, key => key == nameof(LoginRequest) ? authTarget : apiTarget);
```
