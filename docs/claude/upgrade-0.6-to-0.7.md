# Upgrading from 0.6.x to 0.7.0

## RunAsync no longer throws on step execution failures

`JsonWorkflowRunner.RunAsync` now returns execution errors as structured data on `StepResult.Error` instead of throwing. `WorkflowResult.Passed` is `false` when a step fails.

```csharp
// Before — had to catch exceptions to detect execution failures
try
{
    var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);
    if (!result.Passed) { /* assertion failure */ }
}
catch (JsonWorkflowException ex) { /* execution failure — couldn't distinguish from infrastructure */ }

// After — structured result, no exceptions for step failures
var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);
if (!result.Passed)
{
    var error = result.Steps.FirstOrDefault(s => s.Error is not null)?.Error;
    if (error is not null)
    {
        // error.IsTransient — true for 503, 504, 429, 404, network errors
    }
    else
    {
        // result.AssertionErrors has the details
    }
}
```

`ThrowIfFailed()` still works for both cases — it throws on execution errors and assertion failures.

## StepResult gains Error field

`StepResult` has a new optional `Error` field:

```csharp
public record StepResult(string StepName, object? Request, object? Response, StepError? Error = null);
public record StepError(string Message, bool IsTransient);
```

The default is `null` (backward compatible for code that constructs `StepResult` with three arguments).

## Poll steps retry on transient errors

Poll steps now retry automatically on transient HTTP errors (503, 504, 429, 404, network failures) within the timeout window. Non-transient errors (400, 401, 500, etc.) fail immediately.

## HttpExecutor is instance-based

`HttpExecutor` is now an instance bound to a base URL, not a static class. Static helpers (`Deserialize`, `SerializeOptions`, `DeserializeOptions`) are still available.

## IHttpStep.RunAsync takes HttpExecutor

`IHttpStep<TResponse>.RunAsync` and `RunRawAsync` take `HttpExecutor executor` instead of `string baseUrl`.
