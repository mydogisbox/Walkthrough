# Walkthrough — Development Guide

---

## Running tests

Always use `./test.sh`. It starts the sample API, runs all test projects, and tears the API down. Do not run `dotnet test` directly — integration tests depend on the API being up.

Run tests after every change to verify nothing is broken.

---

## Publishing

Use `./publish-local.sh [patch|minor|major|x.y.z]` to bump the version, pack all three projects, and write `.nupkg` files to `./nupkgs`. Version is the single source of truth in `src/Directory.Build.props`.

Then use `./publish-nuget.sh [x.y.z]` to push that version's packages from `./nupkgs` to nuget.org. With no argument it reads the current version from `src/Directory.Build.props` and pushes only the packages matching it — never the whole directory, which accumulates every version ever packed.

The two steps are deliberately separate. `publish-local.sh` bumps on every run, so folding the push into it would burn a version number whenever a push failed, and versions on nuget.org are permanent. Keeping them apart makes publishing re-runnable.

The API key is read from the macOS Keychain, so it never appears in a command line, a dotfile, or shell history. Store it once:

```bash
security add-generic-password -a "$USER" -s nuget-api-key -U -w
```

Override the defaults with `NUGET_KEYCHAIN_SERVICE` (default `nuget-api-key`) or `NUGET_SOURCE` (default nuget.org). Pointing `NUGET_SOURCE` at a local folder is a safe way to rehearse a push without touching nuget.org.

---

## Architecture

```
Walkthrough.Core
├── WorkflowRequest<TResponse>          — transport-agnostic base record; base type used by ITarget
├── BuildableRequest                    — non-generic marker base for array item builders
├── BuildableRequest<TResponse>         — generic base; TResponse is the resolved snapshot type returned by BuildAsync
├── WorkflowContext                     — pure state bag: captures and accumulations only; no execution logic
├── ITarget                             — execute a request against a target; CanHandle(string key) for dispatch
├── WorkflowRunner                      — routes to targets, captures responses, orchestrates polling and building
├── StepError                           — transport-agnostic error: Message + IsTransient
├── IFieldValue<T>                      — interface for resolvable field values
├── FieldValues                         — Static(), Generated(), From() factories
├── FieldValueResolver                  — reflection-based resolver
└── Target<TSelf, TStep>                — base class for targets; manages step registration (Register, Register<T>, CanHandle, GetStep)

Walkthrough.Http
├── HttpTarget : ITarget                — sends requests over HTTP; steps registered via Register<TStep>()
├── HttpExecutor                        — instance-based HTTP transport bound to a base URL; TrySendAsync (non-throwing) / SendAsync / SendRawAsync
├── HttpSendResult                      — IsSuccess, StatusCode, Body, IsTransient (503/504/429/404 + network errors)
├── HttpStepException                   — thrown by SendAsync on failure; carries StatusCode
├── HttpStep                            — abstract base; internal constructor prevents direct external subclassing
├── IHttpStep                           — static abstract Method and Path; declared on every concrete step class
├── IHttpStep<TResponse>                — RunAsync / RunRawAsync dispatch interface; implemented by HttpStep<,,>
└── HttpStep<TRequest, TResponse, TSelf> — CRTP step base; TSelf : IHttpStep; declares static Method, Path; override MapBody/MapQuery/MapHeaders as needed

Walkthrough.Json
├── JsonWorkflowRunner             — pure engine: step execution, path resolution, assertion evaluation
├── JsonHttpTarget : HttpTarget    — untyped execution from TargetDefinition (JSON config); ExecuteAsync returns (Response, StepError?)
├── JsonWorkflowTestBase           — thin xUnit wrapper over the runner
├── WorkflowDefinition             — all JSON model types
├── WorkflowResult                 — WorkflowName, Passed, Steps, AssertionErrors, Captures; ThrowIfFailed()
├── StepResult                     — StepName, Request, Response, Error (StepError? — null on success)
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
- `docs/claude/upgrade-0.6-to-0.7.md` — migration guide (0.6 → 0.7): structured errors, poll transient retry, HttpExecutor instance
- `docs/claude/upgrade-0.5-to-0.6.md` — migration guide (0.5 → 0.6): IWorkflowRequest removal, type-name captures
- `docs/claude/upgrade-0.4-to-0.5.md` — migration guide (0.4 → 0.5)
- `docs/claude/upgrade-0.3-to-0.4.md` — migration guide (0.3 → 0.4)
