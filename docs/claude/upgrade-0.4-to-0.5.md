# Upgrade guide: 0.4.0 → 0.5.0

Two breaking changes. Both are mechanical find-and-replace with no logic changes required.

---

## 1. `HasCapture` replaced by `GetOrDefault<T>`

### What changed

`WorkflowContext.HasCapture(string)` is removed. Use `GetOrDefault<T>(string)` instead — it returns the captured response or `null` if the step hasn't run, so the check and the value are retrieved in one call.

### Before

```csharp
["Authorization"] = From(ctx => ctx.HasCapture("login")
    ? $"Bearer {ctx.Get<LoginResponse>("login").Token}"
    : "")
```

### After

```csharp
["Authorization"] = From(ctx => $"Bearer {ctx.GetOrDefault<LoginResponse>("login")?.Token ?? ""}")
```

### Migration rule

Replace every `HasCapture` guard + `Get` pair:

```csharp
// Before
ctx.HasCapture("stepName") ? ctx.Get<T>("stepName").Field : fallback

// After
ctx.GetOrDefault<T>("stepName")?.Field ?? fallback
```

---

## 2. `StepResult` gains a `Request` field

### What changed

`StepResult` is a positional record. A new `Request` field was inserted between `StepName` and `Response`:

```csharp
// Before
public record StepResult(string StepName, object? Response);

// After
public record StepResult(string StepName, object? Request, object? Response);
```

### Migration rule

Any code that constructs `StepResult` directly or deconstructs it positionally needs updating.

**Construction** (uncommon — this type is normally only produced by the runner):

```csharp
// Before
new StepResult(name, response)

// After
new StepResult(name, request, response)
```

**Positional deconstruction:**

```csharp
// Before
var (stepName, response) = result;

// After
var (stepName, request, response) = result;
```

Named property access (`result.StepName`, `result.Response`) is unaffected.

`Request` holds the resolved request payload (`Dictionary<string, object?>`) for HTTP steps, and `null` for build steps.
