# Walkthrough — Development Guide

---

## Running tests

Always use `./test.sh`. It starts the sample API, runs all test projects, and tears the API down. Do not run `dotnet test` directly — integration tests depend on the API being up.

Run tests after every change to verify nothing is broken.

---

## Architecture

```
Walkthrough.Core
├── WorkflowRequest<TResponse>          — transport-agnostic base record; base type used by ITarget
├── WorkflowRequest<TResponse, TSelf>   — CRTP middle layer; TSelf : IWorkflowRequest gives access to static StepName
├── IWorkflowRequest                    — static abstract StepName { get; }; implemented by every concrete request
├── BuildableRequest                    — non-generic marker base for array item builders
├── BuildableRequest<TResponse>         — generic base; TResponse is the resolved snapshot type returned by BuildAsync
├── WorkflowContext                     — pure state bag: captures and accumulations only; no execution logic
├── ITarget                             — execute a request against a target; implemented by HttpTarget or any custom class
├── WorkflowRunner                      — orchestrates execution: ExecuteAsync, PollAsync, BuildAsync
├── IFieldValue<T>                      — interface for resolvable field values
├── FieldValues                         — Static(), Generated(), From() factories
├── FieldValueResolver                  — reflection-based resolver
└── Target<TSelf, TStep>                — base class for targets; manages step registration (Register, Register<T>, CanHandle, GetStep)

Walkthrough.Http
├── HttpTarget : ITarget                — sends requests over HTTP; steps registered via Register<TStep>()
├── HttpExecutor                        — shared HTTP send/deserialize logic
├── HttpStep                            — abstract base; internal constructor prevents direct external subclassing
├── IHttpStep                           — static abstract Method and Path; declared on every concrete step class
├── IHttpStep<TResponse>                — RunAsync / RunRawAsync dispatch interface; implemented by HttpStep<,,>
└── HttpStep<TRequest, TResponse, TSelf> — CRTP step base; TSelf : IHttpStep; declares static Method, Path; override MapBody/MapQuery/MapHeaders as needed

Walkthrough.Json
├── JsonWorkflowRunner             — pure engine: step execution, path resolution, assertion evaluation
├── JsonWorkflowTestBase           — thin xUnit wrapper over the runner
├── WorkflowDefinition             — all JSON model types
├── WorkflowResult                 — WorkflowName, Passed, Steps, AssertionErrors, Captures; ThrowIfFailed()
└── StepResult                     — StepName, Request (resolved payload; null for build steps), Response
└── JsonValueResolver              — FromJsonValue, JsonElementToObject, field value types
```

Sample structure:

```
samples/Walkthrough.SampleWorkflows/
├── Requests/
│   ├── Login.cs
│   ├── User.cs
│   └── Order.cs
├── WalkthroughTestBase.cs
└── WorkflowTests/
    ├── OrderWorkflowTests.cs
    └── Json/
        ├── JsonOrderWorkflowTests.cs
        ├── sample-api.target.json
        ├── Contracts/
        │   ├── auth.contracts.json
        │   ├── order.contracts.json
        │   └── user.contracts.json
        └── *.workflow.json
```

---

## Testing strategy

Prefer testing through the public surface:

- **Path resolution / `From` references** — construct a `Dictionary<string, object?>` captures dict and call `new FromJsonValue("path").Resolve(captures)`. No need for `InternalsVisibleTo`.
- **Assertions end-to-end** — use `JsonWorkflowRunner.RunAsync(workflow, contracts, targets)` where `contracts` is `Dictionary<string, StepContractDefinition>` and `targets` is `List<TargetDefinition>`. Pass `[]` for targets when using only build steps (no HTTP required). Check `WorkflowResult.Passed` and `AssertionErrors`.
- **Full JSON workflow tests** — create a `.workflow.json` file and add a `[Fact]` to `JsonOrderWorkflowTests` (or a new `JsonWorkflowTestBase` subclass). These hit the live API.

Only reach for lower-level testing if the above is genuinely insufficient.

---

## Consumer docs

The published Claude guidance lives in `docs/claude/` and is copied into consuming projects on package restore. Edit those files when the public API or recommended patterns change.

- `docs/claude/claude.md` — consumer entrypoint: library overview and philosophy
- `docs/claude/csharp-style.md` — fluent C# API patterns (WorkflowRunner, HttpTarget, request/step types, field values)
- `docs/claude/json-style.md` — JSON workflow and contract patterns, including JsonWorkflowRunner and its result types
- `docs/claude/upgrade-0.4-to-0.5.md` — migration guide (0.4 → 0.5)
- `docs/claude/upgrade-0.3-to-0.4.md` — migration guide (0.3 → 0.4)
