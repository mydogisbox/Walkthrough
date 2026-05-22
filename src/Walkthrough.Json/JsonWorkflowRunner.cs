using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Walkthrough.Http;

namespace Walkthrough.Json;

/// <summary>
/// Pure workflow execution engine. Callers supply request definitions and target base URLs;
/// executes all steps, evaluates assertions, and returns a WorkflowResult.
/// No xUnit dependency. Runnable from tests, CLI, or API.
/// </summary>
public class JsonWorkflowRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── Loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads a workflow file and merges step contracts from <paramref name="contractPaths"/>
    /// (resolved relative to the workflow file's directory when not absolute).
    /// </summary>
    public static (WorkflowDefinition Workflow, Dictionary<string, StepContractDefinition> Contracts) Load(
        string workflowPath,
        IReadOnlyList<string> contractPaths)
    {
        var workflowJson = File.ReadAllText(workflowPath);
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(workflowJson, JsonOptions)
            ?? throw new JsonWorkflowException($"Failed to deserialize workflow from '{workflowPath}'.");

        var workflowDir = Path.GetDirectoryName(Path.GetFullPath(workflowPath))!;
        var contracts = LoadContractFiles(contractPaths, workflowDir);

        return (workflow, contracts);
    }

    /// <summary>
    /// Loads a list of .contracts.json files and merges their step contract definitions.
    /// Paths are resolved relative to baseDir.
    /// </summary>
    public static Dictionary<string, StepContractDefinition> LoadContractFiles(
        IReadOnlyList<string> contractPaths,
        string baseDir)
    {
        var merged = new Dictionary<string, StepContractDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in contractPaths)
        {
            var fullPath = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(baseDir, relativePath);

            var json = File.ReadAllText(fullPath);
            var contracts = JsonSerializer.Deserialize<ContractsDefinition>(json, JsonOptions)
                ?? throw new JsonWorkflowException($"Failed to deserialize contracts from '{fullPath}'.");

            foreach (var (name, def) in contracts.Steps)
                merged[name] = def;
        }

        return merged;
    }

    /// <summary>
    /// Loads a list of target files. Each file is a single <see cref="TargetDefinition"/>.
    /// Paths are resolved relative to baseDir.
    /// </summary>
    public static List<TargetDefinition> LoadTargetFiles(
        IReadOnlyList<string> targetPaths,
        string baseDir)
    {
        var targets = new List<TargetDefinition>();

        foreach (var relativePath in targetPaths)
        {
            var fullPath = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(baseDir, relativePath);

            var json = File.ReadAllText(fullPath);
            var target = JsonSerializer.Deserialize<TargetDefinition>(json, JsonOptions)
                ?? throw new JsonWorkflowException($"Failed to deserialize target from '{fullPath}'.");

            targets.Add(target);
        }

        return targets;
    }

    // ── Running ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads workflow files and indexes them by their <c>name</c> field for use as named sub-workflows.
    /// </summary>
    public static Dictionary<string, WorkflowDefinition> LoadNamedWorkflows(IReadOnlyList<string> paths)
    {
        var result = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var json = File.ReadAllText(path);
            var wf = JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonOptions)
                ?? throw new JsonWorkflowException($"Failed to deserialize workflow from '{path}'.");
            result[wf.Name] = wf;
        }
        return result;
    }

    /// <summary>
    /// Executes a workflow given pre-loaded step contracts and target definitions.
    /// </summary>
    public static async Task<WorkflowResult> RunAsync(
        WorkflowDefinition workflow,
        Dictionary<string, StepContractDefinition> contracts,
        List<TargetDefinition> targets,
        IReadOnlyDictionary<string, WorkflowDefinition>? namedWorkflows = null)
    {
        var captures = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var stepResults = await ExecuteStepsAsync(workflow.Steps, contracts, targets, captures, namedWorkflows ?? new Dictionary<string, WorkflowDefinition>());

        var assertionErrors = workflow.Assertions is not null
            ? EvaluateAssertions(workflow.Assertions, captures)
            : [];

        return new WorkflowResult(
            workflow.Name,
            assertionErrors.Count == 0,
            stepResults,
            assertionErrors,
            captures);
    }

    /// <summary>
    /// Loads the workflow, contract, and target files from disk.
    /// </summary>
    public static Task<WorkflowResult> RunAsync(
        string workflowPath,
        IReadOnlyList<string> contractPaths,
        IReadOnlyList<string>? targetPaths = null,
        IReadOnlyList<string>? sharedWorkflowPaths = null)
    {
        var baseDir = Directory.GetCurrentDirectory();
        var workflowJson = File.ReadAllText(workflowPath);
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(workflowJson, JsonOptions)
            ?? throw new JsonWorkflowException($"Failed to deserialize workflow from '{workflowPath}'.");
        var contracts = LoadContractFiles(contractPaths, baseDir);
        var targets = targetPaths is { Count: > 0 }
            ? LoadTargetFiles(targetPaths, baseDir)
            : [];
        var namedWorkflows = sharedWorkflowPaths is { Count: > 0 }
            ? LoadNamedWorkflows(sharedWorkflowPaths)
            : new Dictionary<string, WorkflowDefinition>();
        return RunAsync(workflow, contracts, targets, namedWorkflows);
    }

    // ── Step execution ───────────────────────────────────────────────────────

    private static async Task<List<StepResult>> ExecuteStepsAsync(
        IEnumerable<StepInvocation> steps,
        Dictionary<string, StepContractDefinition> contracts,
        List<TargetDefinition> targets,
        Dictionary<string, object?> captures,
        IReadOnlyDictionary<string, WorkflowDefinition> namedWorkflows)
    {
        var results = new List<StepResult>();
        foreach (var invocation in steps)
        {
            if (invocation.Step is not null)
                results.Add(await ExecuteStepAsync(invocation, contracts, targets, captures));
            else if (invocation.Build is not null)
                results.Add(BuildItem(invocation, contracts, captures));
            else if (invocation.Poll is not null)
                results.Add(await PollStepAsync(invocation, contracts, targets, captures));
            else if (invocation.Workflow is not null)
                results.AddRange(await ExecuteNestedWorkflowAsync(invocation.Workflow, contracts, targets, captures, namedWorkflows));
            else
                throw new JsonWorkflowException("Each step must have 'step', 'build', 'poll', or 'workflow'.");
        }
        return results;
    }

    private static async Task<List<StepResult>> ExecuteNestedWorkflowAsync(
        string workflowRef,
        Dictionary<string, StepContractDefinition> contracts,
        List<TargetDefinition> targets,
        Dictionary<string, object?> captures,
        IReadOnlyDictionary<string, WorkflowDefinition> namedWorkflows)
    {
        WorkflowDefinition nested;
        if (namedWorkflows.TryGetValue(workflowRef, out var named))
        {
            nested = named;
        }
        else
        {
            var json = File.ReadAllText(workflowRef);
            nested = JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonOptions)
                ?? throw new JsonWorkflowException($"Failed to deserialize nested workflow from '{workflowRef}'.");
        }
        return await ExecuteStepsAsync(nested.Steps, contracts, targets, captures, namedWorkflows);
    }

    private static async Task<StepResult> ExecuteStepAsync(
        StepInvocation invocation,
        Dictionary<string, StepContractDefinition> contracts,
        List<TargetDefinition> targets,
        Dictionary<string, object?> captures)
    {
        var stepName = invocation.Step!;

        contracts.TryGetValue(stepName, out var contract);

        var targetDef = targets.FirstOrDefault(t => t.Steps?.ContainsKey(stepName) == true);
        if (targetDef is null)
            throw new JsonWorkflowException(
                $"Step '{stepName}' not found in any loaded target. " +
                $"Loaded targets cover: [{string.Join(", ", targets.SelectMany(t => t.Steps?.Keys ?? (IEnumerable<string>)[]))}]");

        var targetStep = targetDef.Steps![stepName];

        var pathParams    = ResolveFieldGroup(targetStep.PathParams, invocation.PathParams, captures);
        var rawQuery      = ResolveFieldGroup(targetStep.Query,       invocation.Query,      captures);
        var queryParams   = rawQuery.ToDictionary(
            kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
        var rawHeaders  = ResolveFieldGroup(targetDef.Headers,      null,                  captures);
        foreach (var kv in ResolveFieldGroup(targetStep.Headers, invocation.Headers, captures))
            rawHeaders[kv.Key] = kv.Value;
        var headers = rawHeaders.ToDictionary(
            kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
        var bodyFields  = MergeAndResolve(contract?.Defaults, invocation.With, captures);
        var baseUrl = targetDef.BaseUrl;
        var method = new HttpMethod(targetStep.Method.ToUpper());

        if (invocation.CaptureRequestAs is { } requestKey)
            captures[requestKey] = bodyFields;

        if (invocation.CaptureFullResponseAs is { } fullResponseKey)
        {
            var (statusCode, rawBody) = await HttpExecutor.SendRawAsync(
                baseUrl, method, targetStep.Path, pathParams, queryParams, bodyFields, headers);

            object? parsedBody;
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                parsedBody = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray()
                        .Select(e => JsonValueResolver.JsonElementToObject(e))
                        .ToList<object?>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rawBody, HttpExecutor.DeserializeOptions);
            }
            catch
            {
                parsedBody = rawBody;
            }

            var fullResponse = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = statusCode,
                ["body"] = parsedBody
            };

            captures[fullResponseKey] = fullResponse;
            return new StepResult(fullResponseKey, bodyFields, fullResponse);
        }

        var responseJson = await HttpExecutor.SendAsync(
            baseUrl, method, targetStep.Path, pathParams, queryParams, bodyFields, headers);

        using var doc2 = JsonDocument.Parse(responseJson);
        object? captured = doc2.RootElement.ValueKind == JsonValueKind.Array
            ? doc2.RootElement.EnumerateArray()
                .Select(e => JsonValueResolver.JsonElementToObject(e))
                .ToList<object?>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseJson, HttpExecutor.DeserializeOptions);

        var captureName = invocation.CaptureAs ?? stepName;
        captures[captureName] = captured;

        return new StepResult(captureName, bodyFields, captured);
    }

    private static StepResult BuildItem(
        StepInvocation invocation,
        Dictionary<string, StepContractDefinition> contracts,
        Dictionary<string, object?> captures)
    {
        var buildName = invocation.Build!;

        if (!contracts.TryGetValue(buildName, out var contract))
            throw new JsonWorkflowException(
                $"Build step '{buildName}' not found in loaded contract files.");

        var accumulationKey = contract.AccumulateAs
            ?? throw new JsonWorkflowException(
                $"Build step '{buildName}' must specify 'accumulateAs' in its contract definition.");

        var resolvedFields = MergeAndResolve(contract.Defaults, invocation.With, captures);
        if (!captures.TryGetValue(accumulationKey, out var existing) ||
            existing is not List<Dictionary<string, object?>> list)
        {
            list = [];
            captures[accumulationKey] = list;
        }
        list.Add(resolvedFields);

        var captureName = invocation.CaptureAs ?? buildName;
        captures[captureName] = resolvedFields;

        return new StepResult(captureName, null, resolvedFields);
    }

    private static async Task<StepResult> PollStepAsync(
        StepInvocation invocation,
        Dictionary<string, StepContractDefinition> contracts,
        List<TargetDefinition> targets,
        Dictionary<string, object?> captures)
    {
        var stepName = invocation.Poll!;
        var executeInvocation = new StepInvocation { Step = stepName, CaptureAs = invocation.CaptureAs, With = invocation.With, Headers = invocation.Headers };
        var deadline = DateTime.UtcNow.AddMilliseconds(invocation.TimeoutMs);

        while (true)
        {
            var result = await ExecuteStepAsync(executeInvocation, contracts, targets, captures);

            if (invocation.Until is null || EvaluateAssertions([invocation.Until], captures).Count == 0)
                return result;

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new JsonWorkflowException(
                    $"Poll step '{stepName}' timed out after {invocation.TimeoutMs}ms.");

            await Task.Delay((int)Math.Min(invocation.IntervalMs, remaining.TotalMilliseconds));
        }
    }

    private static Dictionary<string, object?> MergeAndResolve(
        Dictionary<string, FieldValueDefinition>? defaults,
        Dictionary<string, FieldValueDefinition>? overrides,
        Dictionary<string, object?> captures)
    {
        var resolvedDefaults = (defaults ?? []).ToDictionary(
            kv => kv.Key,
            kv => JsonValueResolver.Resolve(kv.Value).Resolve(captures),
            StringComparer.OrdinalIgnoreCase);

        var resolvedOverrides = (overrides ?? []).ToDictionary(
            kv => kv.Key,
            kv => JsonValueResolver.Resolve(kv.Value).Resolve(captures),
            StringComparer.OrdinalIgnoreCase);

        return DeepMerge(resolvedDefaults, resolvedOverrides);
    }

    private static Dictionary<string, object?> DeepMerge(
        Dictionary<string, object?> defaults,
        Dictionary<string, object?> overrides)
    {
        var result = new Dictionary<string, object?>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, overrideVal) in overrides)
        {
            if (result.TryGetValue(key, out var defaultVal) &&
                defaultVal is Dictionary<string, object?> defaultDict &&
                overrideVal is Dictionary<string, object?> overrideDict)
            {
                result[key] = DeepMerge(defaultDict, overrideDict);
            }
            else
            {
                result[key] = overrideVal;
            }
        }
        return result;
    }

    private static Dictionary<string, object?> ResolveFieldGroup(
        Dictionary<string, FieldValueDefinition>? defs,
        Dictionary<string, FieldValueDefinition>? overrides,
        Dictionary<string, object?> captures)
    {
        var merged = new Dictionary<string, FieldValueDefinition>(StringComparer.OrdinalIgnoreCase);
        if (defs is not null)
            foreach (var (k, v) in defs) merged[k] = v;
        if (overrides is not null)
            foreach (var (k, v) in overrides) merged[k] = v;
        return merged.ToDictionary(
            kv => kv.Key,
            kv => JsonValueResolver.Resolve(kv.Value).Resolve(captures),
            StringComparer.OrdinalIgnoreCase);
    }

    // ── Assertions ───────────────────────────────────────────────────────────

    private static List<string> EvaluateAssertions(
        List<AssertionDefinition> assertions,
        Dictionary<string, object?> captures)
    {
        var errors = new List<string>();

        foreach (var assertion in assertions)
        {
            if (assertion.Equal is { Count: 2 })
            {
                var left = ResolveAssertionExpr(assertion.Equal[0], captures);
                var right = ResolveAssertionExpr(assertion.Equal[1], captures);
                if (!string.Equals(left?.ToString(), right?.ToString(), StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Expected '{assertion.Equal[0]}' ({left}) to equal '{assertion.Equal[1]}' ({right}).");
            }
            else if (assertion.NotEqual is { Count: 2 })
            {
                var left = ResolveAssertionExpr(assertion.NotEqual[0], captures);
                var right = ResolveAssertionExpr(assertion.NotEqual[1], captures);
                if (string.Equals(left?.ToString(), right?.ToString(), StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Expected '{assertion.NotEqual[0]}' to not equal '{assertion.NotEqual[1]}' but both were '{left}'.");
            }
            else if (assertion.Single is not null)
            {
                var value = ResolveAssertionExpr(assertion.Single, captures);
                var count = CountItems(value);
                if (count != 1)
                    errors.Add($"Expected '{assertion.Single}' to have exactly 1 item but found {count}.");
            }
            else if (assertion.Empty is not null)
            {
                var value = ResolveAssertionExpr(assertion.Empty, captures);
                var count = CountItems(value);
                if (count != 0)
                    errors.Add($"Expected '{assertion.Empty}' to be empty but found {count} items.");
            }
            else if (assertion.Count is { Count: 2 })
            {
                var value = ResolveAssertionExpr(assertion.Count[0], captures);
                var actual = CountItems(value);
                if (!int.TryParse(assertion.Count[1], out var expected))
                    errors.Add($"Invalid count value '{assertion.Count[1]}' — must be an integer.");
                else if (actual != expected)
                    errors.Add($"Expected '{assertion.Count[0]}' to have {expected} item(s) but found {actual}.");
            }
            else if (assertion.NotEmpty is not null)
            {
                var value = ResolveAssertionExpr(assertion.NotEmpty, captures);
                var count = CountItems(value);
                if (count == 0)
                    errors.Add($"Expected '{assertion.NotEmpty}' to not be empty.");
            }
        }

        return errors;
    }

    private static object? ResolveAssertionExpr(string expr, Dictionary<string, object?> captures)
        => expr.StartsWith('$') ? ResolveCapturePath(expr[1..], captures)
         : (object?)expr;

    internal static object? ResolveCapturePath(string path, Dictionary<string, object?> captures)
    {
        var segments = TokenizePath(path);
        if (segments.Count == 0) return null;

        if (!captures.TryGetValue(segments[0], out var current)) return null;

        for (int i = 1; i < segments.Count; i++)
        {
            if (current is null) return null;
            current = ResolveSegment(current, segments[i], captures);
        }

        return current;
    }

    // Splits a path like "step.items[?id=other.id].name" into ["step", "items", "[?id=other.id]", "name"].
    // Dots inside bracket expressions are treated as part of the expression, not as separators.
    private static List<string> TokenizePath(string path)
    {
        var segments = new List<string>();
        var i = 0;
        var current = new System.Text.StringBuilder();

        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                if (current.Length > 0) { segments.Add(current.ToString()); current.Clear(); }
                var close = path.IndexOf(']', i);
                if (close < 0) break;
                segments.Add(path[i..(close + 1)]);
                i = close + 1;
            }
            else if (path[i] == '.')
            {
                if (current.Length > 0) { segments.Add(current.ToString()); current.Clear(); }
                i++;
            }
            else
            {
                current.Append(path[i++]);
            }
        }

        if (current.Length > 0) segments.Add(current.ToString());
        return segments;
    }

    private static object? ResolveSegment(object current, string segment, Dictionary<string, object?>? captures = null)
    {
        // Bracket segment, e.g. "[0]" or "[?field=value]"
        if (segment.StartsWith('[') && segment.EndsWith(']'))
        {
            var inner = segment[1..^1];
            var eqIdx = inner.IndexOf('=');

            // Field lookup: [?field=value] — value may be a capture path
            if (inner.StartsWith('?') && eqIdx > 1)
            {
                var field = inner[1..eqIdx];
                var rawValue = inner[(eqIdx + 1)..];
                var resolvedValue = captures is not null && (rawValue.Contains('.') || rawValue.Contains('['))
                    ? ResolveCapturePath(rawValue, captures)?.ToString() ?? rawValue
                    : rawValue;
                var items = current switch
                {
                    System.Collections.IEnumerable e and not string => e.Cast<object?>(),
                    _ => null
                };
                return items?.FirstOrDefault(item =>
                    item is not null &&
                    string.Equals(ResolveSegment(item, field)?.ToString(), resolvedValue, StringComparison.OrdinalIgnoreCase));
            }

            // Numeric index: [0]
            if (!int.TryParse(inner, out var idx)) return null;
            return current switch
            {
                List<object?> list       => idx >= 0 && idx < list.Count ? list[idx] : null,
                object?[] arr            => idx >= 0 && idx < arr.Length ? arr[idx] : null,
                System.Collections.IList ilist => idx >= 0 && idx < ilist.Count ? ilist[idx] : null,
                JsonElement el when el.ValueKind == JsonValueKind.Array
                                         => idx >= 0 && idx < el.GetArrayLength()
                                            ? JsonValueResolver.JsonElementToObject(el[idx]) : null,
                _                        => null,
            };
        }

        // Property/key segment
        return current switch
        {
            Dictionary<string, object?> objDict => objDict.TryGetValue(
                objDict.Keys.FirstOrDefault(k =>
                    string.Equals(k, segment, StringComparison.OrdinalIgnoreCase)) ?? "",
                out var oval) ? oval : null,
            Dictionary<string, JsonElement> dict => dict.Keys.FirstOrDefault(k =>
                string.Equals(k, segment, StringComparison.OrdinalIgnoreCase)) is { } key
                ? JsonValueResolver.JsonElementToObject(dict[key]) : null,
            JsonElement el when el.ValueKind == JsonValueKind.Object =>
                el.TryGetProperty(segment, out var jprop)
                ? JsonValueResolver.JsonElementToObject(jprop) : null,
            _ => current.GetType().GetProperty(segment,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?.GetValue(current),
        };
    }

    private static int CountItems(object? value)
    {
        if (value is System.Collections.IEnumerable enumerable and not string)
            return enumerable.Cast<object>().Count();
        if (value is JsonElement el && el.ValueKind == JsonValueKind.Array)
            return el.GetArrayLength();
        return value is null ? 0 : 1;
    }
}
