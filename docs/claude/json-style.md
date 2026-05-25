# JSON style

## Workflow file

```json
{
  "name": "PlacedOrder_CanBeRetrieved",
  "steps": [
    { "step": "login" },
    { "step": "createUser" },
    { "build": "addOrderItem" },
    { "step": "createOrder" },
    { "step": "getOrder" }
  ],
  "assertions": [
    { "equal": ["$createOrder.id", "$getOrder.id"] },
    { "equal": ["$getOrder.status", "pending"] }
  ]
}
```

---

## Contracts file

Defines the transport-agnostic shape of each step — default body fields and (for build steps) the accumulation key. No HTTP execution details.

```json
{
  "steps": {
    "addOrderItem": {
      "accumulateAs": "orderItems",
      "defaults": {
        "productName": { "static": "Widget" },
        "quantity":    { "static": 1 },
        "unitPrice":   { "static": 9.99 }
      }
    },
    "createOrder": {
      "defaults": {
        "userId": { "from": "createUser.id" },
        "items":  { "from": "orderItems" }
      }
    }
  }
}
```

Steps with no body defaults (e.g. `getOrder`) need no entry in the contracts file. Build steps only use contracts — they never need a target entry.

---

## Target file

Defines how to execute steps — base URL, per-step HTTP method/path/pathParams/query/headers. Each file is a single target. The runner finds the right target for a step by scanning which target declares that step name.

```json
{
  "baseUrl": "http://localhost:4200",
  "steps": {
    "login": {
      "method": "POST",
      "path": "/auth/login"
    },
    "createOrder": {
      "method": "POST",
      "path": "/orders",
      "headers": {
        "Authorization": { "template": "Bearer {login.token}" }
      }
    },
    "getOrder": {
      "method": "GET",
      "path": "/orders/{orderId}",
      "pathParams": {
        "orderId": { "from": "createOrder.id" }
      },
      "headers": {
        "Authorization": { "template": "Bearer {login.token}" }
      }
    }
  }
}
```

---

## URL parameters (`pathParams`)

Defined in the target file under the step's entry. Values substitute into `{placeholder}` segments of the path and are never sent in the request body. The key must match the placeholder name exactly.

---

## Query parameters (`query`)

Key-value pairs appended to the URL as a query string. `query` can be combined with `pathParams` in the same step entry:

```json
"searchOrders": {
  "method": "GET",
  "path": "/orders",
  "query": {
    "status": { "static": "pending" },
    "userId": { "from": "createUser.id" }
  }
}
```

Produces: `GET /orders?status=pending&userId=abc-123`

---

## Per-invocation overrides for `pathParams` and `query`

Override values are merged over step-level values (invocation wins for matching keys). `with` (body field overrides), `pathParams`, and `query` can all be combined on the same invocation.

```json
{ "step": "getOrder", "pathParams": { "orderId": { "from": "firstOrder.id" } }, "captureAs": "retrieved" }
```

```json
{ "step": "getUsers", "query": { "role": { "static": "admin" } }, "captureAs": "admins" }
```

---

## Path reference syntax

`from` values and assertion expressions use: `captureKey.property.nested[index].property`

- First segment is always the capture key (step name or `captureAs` value)
- Supports arbitrary nesting: `step.a.b.c`
- Supports numeric index: `step.items[0]`
- Supports field lookup: `step.items[?id=abc-123]` — finds the first element where `id` equals `abc-123` (case-insensitive)
- Supports dynamic field lookup values: `step.items[?id=other.id]` — the lookup value is resolved as a capture path when it contains `.` or `[`
- Supports combinations: `step.items[?productName=Widget B].quantity`

In assertions, prefix with `$` to resolve as a capture path (e.g., `"$createOrder.id"`). Strings without `$` are treated as literals.

---

## Assertion types

| Type | Example | Meaning |
|------|---------|---------|
| `equal` | `"equal": ["$createOrder.status", "pending"]` | two expressions are equal |
| `notEqual` | `"notEqual": ["$firstOrder.id", "$secondOrder.id"]` | two expressions differ |
| `single` | `"single": "$createOrder.items"` | collection has exactly 1 item |
| `empty` | `"empty": "$createOrder.items"` | collection is empty |
| `notEmpty` | `"notEmpty": "$createOrder.id"` | value is present / collection is non-empty |
| `count` | `"count": ["$createOrder.items", "2"]` | collection has exactly N items |

Assertion expressions must be prefixed with `$` to be resolved as a capture path. Strings without `$` are treated as literals.

---

## Field value types

```json
{ "static": "some value" }                   // literal — any JSON primitive or array/object
{ "generated": "guid" }                      // built-in generators: guid, email, int, decimal
{ "from": "login.token" }                    // capture path
{ "template": "Bearer {login.token}" }       // string with {capture.path} placeholders
```

`template` substitutes `{capture.path}` placeholders with resolved values. Use `{{` and `}}` for literal braces. Throws if any placeholder cannot be resolved.

`from` accepts an optional `default` used when the referenced step has not run:

```json
{ "from": "createUser.id", "default": { "static": "guest" } }
```

- If `createUser` was never executed, the default resolves and is used.
- If `createUser` ran but `id` is missing or null, a `JsonWorkflowException` is thrown.
- The `default` supports `static`, `generated`, `from`, or `template`.

When `static` contains a JSON object or array, each value is resolved recursively as a `FieldValueDefinition`. An object with a `static`, `from`, `generated`, or `template` key is treated as a field value definition; all other objects are structural nodes. This allows `from` and `generated` at any depth:

```json
"contact": { "static": {
  "primary": { "static": {
    "address": { "static": {
      "street": { "static": "123 Main St" },
      "city":   { "from": "user.city" }
    }}
  }}
}}
```

```json
"tags": { "static": [{ "from": "step.tag" }, { "static": "fixed-tag" }] }
```

```json
"pair": { "static": [{ "template": "{step.id}.field" }, "literal"] }
```

---

## Nested object defaults and partial overrides

When a `with` block specifies a nested object, it is **deep-merged** with the default: override wins for matching keys at every level, and unspecified keys are filled from the default.

```json
// workflow file — override only what matters
{
  "step": "updateUserAddress",
  "with": {
    "contact": { "static": {
      "primary": { "static": {
        "address": { "static": {
          "city":   { "static": "Boston" },
          "region": { "static": { "state": { "static": "MA" } }}
        }}
      }}
    }}
  }
}
```

`street` and `country` are not specified, so they come from the step defaults.

**Limitation:** Deep merge only applies to objects. If both default and override resolve to an array, the override replaces the default entirely. To vary array contents across invocations, use `build` steps and reference the accumulation via `from`.

---

## Headers

Three levels, merged in order (later wins for matching keys):

1. **Target-level** — root of the target file under `headers`; sent with every request to that target.
2. **Step-level** — target file under `steps.<name>.headers`; sent with every invocation of that step.
3. **Invocation-level** — workflow file under `headers` on a `step` or `poll` invocation; applies to that call only.

```json
// step-level in target file
"createItem": {
  "method": "POST",
  "path": "/items",
  "headers": { "X-Api-Version": { "static": "2" } }
}
```

```json
// invocation-level in workflow file
{ "step": "createItem", "headers": { "X-Request-Id": { "from": "session.requestId" } } }
```

For C# headers, see [csharp-style.md](csharp-style.md#headers).

---

## `poll` — polling a step until a condition is met

Re-executes a step on an interval until an `until` assertion passes or the timeout is reached.

```json
{
  "poll": "getOrder",
  "until": { "equal": ["$getOrder.status", "shipped"] },
  "intervalMs": 500,
  "timeoutMs": 10000
}
```

- `intervalMs` — delay between attempts (default: 500)
- `timeoutMs` — max wait before throwing (default: 10000)
- `captureAs` — works the same as on `step` invocations
- If `until` is omitted the step executes once and succeeds immediately
- Transient HTTP errors (503, 504, 429, 404, network failures) are retried automatically within the timeout. Non-transient errors (400, 401, 500, etc.) fail immediately.

---

## `workflow` — nesting workflows

A step can embed another workflow by name. Its steps execute against the same captures and targets as the parent; its assertions are skipped.

```json
{
  "name": "PlaceOrder",
  "steps": [
    { "workflow": "SetupUser" },
    { "build": "addOrderItem" },
    { "step": "createOrder" }
  ]
}
```

Named workflows are pre-loaded by the test class via `SharedWorkflowPaths`. The `workflow` value is matched against each file's `name` field; if no match is found it is treated as a file path. Nesting is recursive.

```csharp
protected override IReadOnlyList<string> SharedWorkflowPaths =>
[
    "WorkflowTests/Json/setup-user.workflow.json"
];
```

---

## `captureAs` — naming captures explicitly

Without `captureAs`, captures are stored under the step's definition name. Use it when the same step runs more than once:

```json
{ "step": "createOrder", "captureAs": "firstOrder" }
{ "step": "createOrder", "captureAs": "secondOrder" }
```

Use it on build steps to pin a specific item's fields independently of the accumulation:

```json
{ "build": "addOrderItem", "with": { "productName": { "static": "Widget B" } }, "captureAs": "lastItem" }
```

Without `captureAs`, each build overwrites the previous individual capture for that step name.

---

## xUnit runner

```csharp
public class JsonOrderWorkflowTests : JsonWorkflowTestBase
{
    protected override IReadOnlyList<string> ContractPaths =>
    [
        "WorkflowTests/Json/Contracts/auth.contracts.json",
        "WorkflowTests/Json/Contracts/order.contracts.json"
    ];

    protected override IReadOnlyList<string> TargetPaths =>
    [
        "WorkflowTests/Json/sample-api.target.json"
    ];

    [Fact]
    public Task PlacedOrder_CanBeRetrieved() =>
        RunWorkflowAsync("WorkflowTests/Json/retrieve-order.workflow.json");
}
```

`ContractPaths` and `TargetPaths` are resolved relative to the project working directory. A class with only build steps needs no `TargetPaths`.

---

## Capture runtime model

| Source | Key | Type stored in captures |
|--------|-----|------------------------|
| HTTP step response | step name or `captureAs` | `Dictionary<string, JsonElement>` |
| HTTP step full response (status + body) | `captureFullResponseAs` value | `Dictionary<string, object?>` with keys `"status"` (int) and `"body"` |
| HTTP step request payload | `captureRequestAs` value | `Dictionary<string, object?>` |
| Build step — individual result | step name or `captureAs` | `Dictionary<string, object?>` |
| Build step — accumulation | `accumulateAs` value | `List<Dictionary<string, object?>>` |
| JSON array after `JsonElementToObject` | — | `List<object?>` |
| JSON object after `JsonElementToObject` | — | `Dictionary<string, object?>` |

Each build step writes to **two** capture keys: the accumulation list (always, under `accumulateAs`) and the individual result (under the step name or `captureAs`). When the same step name is built multiple times without `captureAs`, the individual result capture is overwritten each time.

### `captureRequestAs`

Captures the resolved request payload under a named key without replacing the response capture:

```json
{ "step": "createUser", "captureRequestAs": "userRequest", "with": { "firstName": { "static": "Jane" } } }
```

After this step, `userRequest.firstName` resolves to `"Jane"` and the response is still captured under `createUser`.

### `captureFullResponseAs`

Captures the raw HTTP response — including the status code — under a named key. Replaces the normal response capture for that invocation (the step name is not populated). Use when you need the status code or the step may return non-2xx without throwing:

```json
{ "step": "deleteUser", "captureFullResponseAs": "deleteResult" }
```

After this step, `deleteResult.status` is the HTTP status code and `deleteResult.body` is the parsed response body. If the body is valid JSON it is parsed; otherwise the raw string is stored.

---

## Choosing a capture strategy

| Situation | What to use |
|-----------|-------------|
| Share common setup steps across multiple workflows | `{ "workflow": "SetupUser" }` |
| Wait for an async result to reach a desired state | `{ "poll": "getOrder", "until": { "equal": ["$getOrder.status", "shipped"] } }` |
| Reference a response field from a step that runs once | Step name: `createUser.id` |
| Same HTTP step runs multiple times; need to tell results apart | `captureAs` on each invocation: `"firstOrder"`, `"secondOrder"` |
| Need a more readable alias for a response | `captureAs: "token"` instead of `login.token` |
| Server doesn't echo a request field back; need it downstream | `captureRequestAs: "userRequest"` then `userRequest.email` |
| Need the HTTP status code, or step may return non-2xx without throwing | `captureFullResponseAs: "result"` then `result.status` / `result.body.field` |
| Inspect a specific item in a built collection | Accumulation index: `orderItems[0].productName` |
| Reference the fields of the most recently built item | Build step name: `addOrderItem.productName` (overwritten each time the step builds without `captureAs`) |
| Pin a specific built item independently of the accumulation | `captureAs` on the build invocation: `"specialItem"` then `specialItem.productName` |

**Key rules:**
- `captureAs` and the step name are mutually exclusive for a given invocation — `captureAs` wins if set.
- `captureRequestAs` is additive — it doesn't replace the response capture; both are available.
- `captureFullResponseAs` replaces the normal response capture for that invocation — the step name is not populated.
- Build steps always write to the `accumulateAs` list regardless of `captureAs`.

---

## WorkflowResult and StepResult

`JsonWorkflowRunner.RunAsync` returns a `WorkflowResult`:

| Field | Type | Description |
|-------|------|-------------|
| `WorkflowName` | `string` | Name of the workflow that ran |
| `Passed` | `bool` | `true` if all assertions passed |
| `Steps` | `List<StepResult>` | One entry per executed step, in order |
| `AssertionErrors` | `List<string>` | Failure messages when `Passed` is `false` |
| `Captures` | `Dictionary<string, object?>` | All captured values at end of workflow |

Each `StepResult` entry:

| Field | Type | Description |
|-------|------|-------------|
| `StepName` | `string` | Capture key for this step (step name or `captureAs`) |
| `Request` | `object?` | Resolved request payload sent (`Dictionary<string, object?>`); `null` for build steps |
| `Response` | `object?` | Deserialized response; `Dictionary<string, object?>` for full-response captures |

`WorkflowResult.ThrowIfFailed()` throws a `JsonWorkflowException` with all assertion errors formatted if `Passed` is `false`.
- Without `captureAs`, repeated builds of the same step overwrite the individual result capture; the accumulation still grows.
