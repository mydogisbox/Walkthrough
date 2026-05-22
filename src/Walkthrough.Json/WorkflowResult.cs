namespace Walkthrough.Json;

/// <summary>
/// The result of running a workflow — step results, assertion errors, and all captures.
/// </summary>
public record WorkflowResult(
    string WorkflowName,
    bool Passed,
    List<StepResult> Steps,
    List<string> AssertionErrors,
    Dictionary<string, object?> Captures
)
{
    public void ThrowIfFailed()
    {
        if (!Passed)
            throw new JsonWorkflowException(
                $"Workflow '{WorkflowName}' failed:\n" +
                string.Join("\n", AssertionErrors.Select(e => $"  - {e}")));
    }
}

/// <summary>
/// The result of a single step execution.
/// </summary>
public record StepResult(string StepName, object? Request, object? Response);
