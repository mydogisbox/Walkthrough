using System.Net;
using System.Text.Json;
using Walkthrough.Core;
using Walkthrough.Http;
using Walkthrough.Json;
using Xunit;

namespace Walkthrough.UnitTests;

// ── WorkflowRunner.PollAsync ─────────────────────────────────────────────

public class WorkflowContextPollTests
{
    private record StatusResponse(string Status);
    private record GetStatusRequest() : WorkflowRequest<StatusResponse>
    {
    }

    private class FakeTarget : ITarget
    {
        private readonly Queue<object> _responses = new();
        public int CallCount { get; private set; }

        public bool CanHandle(string _) => true;
        public void Enqueue<T>(T response) => _responses.Enqueue(response!);

        public Task<TResponse> ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
        {
            CallCount++;
            return Task.FromResult((TResponse)_responses.Dequeue());
        }
    }

    [Fact]
    public async Task ReturnsOnFirstAttempt_WhenConditionImmediatelyMet()
    {
        var fake = new FakeTarget();
        fake.Enqueue(new StatusResponse("Completed"));

        var runner = new WorkflowRunner(new WorkflowContext(), _ => fake);
        var result = await runner.PollAsync(new GetStatusRequest(), r => r.Status == "Completed");

        Assert.Equal("Completed", result.Status);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task Retries_UntilConditionMet()
    {
        var fake = new FakeTarget();
        fake.Enqueue(new StatusResponse("Pending"));
        fake.Enqueue(new StatusResponse("Pending"));
        fake.Enqueue(new StatusResponse("Completed"));

        var runner = new WorkflowRunner(new WorkflowContext(), _ => fake);
        var result = await runner.PollAsync(
            new GetStatusRequest(), r => r.Status == "Completed", intervalMs: 1);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(3, fake.CallCount);
    }

    [Fact]
    public async Task ThrowsWorkflowContextException_OnTimeout()
    {
        var fake = new FakeTarget();
        for (var i = 0; i < 10; i++) fake.Enqueue(new StatusResponse("Pending"));

        var runner = new WorkflowRunner(new WorkflowContext(), _ => fake);
        var ex = await Assert.ThrowsAsync<WorkflowContextException>(() =>
            runner.PollAsync(new GetStatusRequest(), r => r.Status == "Completed",
                intervalMs: 10, timeoutMs: 50));

        Assert.Contains("GetStatusRequest", ex.Message);
    }

    [Fact]
    public async Task CapturesFinalResponse_UnderTypeName()
    {
        var fake = new FakeTarget();
        fake.Enqueue(new StatusResponse("Pending"));
        fake.Enqueue(new StatusResponse("Completed"));

        var context = new WorkflowContext();
        var runner  = new WorkflowRunner(context, _ => fake);
        await runner.PollAsync(new GetStatusRequest(), r => r.Status == "Completed", intervalMs: 1);

        Assert.Equal("Completed", context.Get<StatusResponse>("GetStatusRequest").Status);
    }
}

// ── JsonWorkflowRunner poll step ─────────────────────────────────────────────

public class JsonWorkflowRunnerPollTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly int[] _callCount = new int[1];

    public JsonWorkflowRunnerPollTests()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
    }

    public void Dispose() => _listener.Stop();

    private void ServeSequence(params string[] responses)
        => ServeStatusSequence(responses.Select(r => (200, r)).ToArray());

    private void ServeStatusSequence(params (int StatusCode, string Body)[] responses)
    {
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                var idx = Math.Min(_callCount[0]++, responses.Length - 1);
                var (statusCode, body) = responses[idx];
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = statusCode;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    private (WorkflowDefinition, Dictionary<string, StepContractDefinition>, List<TargetDefinition>) BuildWorkflow(StepInvocation step)
    {
        var contracts = new Dictionary<string, StepContractDefinition>();
        var targets = new List<TargetDefinition>
        {
            new()
            {
                BaseUrl = $"http://127.0.0.1:{_port}",
                Steps = new() { ["getStatus"] = new TargetStepDefinition { Method = "GET", Path = "/status" } }
            }
        };
        var workflow = new WorkflowDefinition(Name: "PollTest", Steps: [step]);
        return (workflow, contracts, targets);
    }

    [Fact]
    public async Task Poll_ResolvesWhenConditionEventuallyMet()
    {
        ServeSequence(
            "{\"status\":\"Pending\"}",
            "{\"status\":\"Pending\"}",
            "{\"status\":\"Completed\"}");

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            Until = new AssertionDefinition { Equal = ["$getStatus.status", "Completed"] },
            IntervalMs = 1,
            TimeoutMs = 5000
        });

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);

        Assert.True(result.Passed);
        Assert.Equal(3, _callCount[0]);
    }

    [Fact]
    public async Task Poll_WithNoUntil_ExecutesOnceAndSucceeds()
    {
        ServeSequence("{\"status\":\"Completed\"}");

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            IntervalMs = 1,
            TimeoutMs = 5000
        });

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);

        Assert.True(result.Passed);
        Assert.Equal(1, _callCount[0]);
    }

    [Fact]
    public async Task Poll_ThrowsJsonWorkflowException_OnTimeout()
    {
        ServeSequence("{\"status\":\"Pending\"}");

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            Until = new AssertionDefinition { Equal = ["$getStatus.status", "Completed"] },
            IntervalMs = 1,
            TimeoutMs = 50
        });

        var ex = await Assert.ThrowsAsync<JsonWorkflowException>(() =>
            JsonWorkflowRunner.RunAsync(workflow, contracts, targets));

        Assert.Contains("getStatus", ex.Message);
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Poll_CapturesFinalResponse_ForSubsequentAssertions()
    {
        ServeSequence(
            "{\"status\":\"Pending\"}",
            "{\"status\":\"Completed\"}");

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            Until = new AssertionDefinition { Equal = ["$getStatus.status", "Completed"] },
            IntervalMs = 1,
            TimeoutMs = 5000
        });

        var withAssertion = workflow with
        {
            Assertions = [new AssertionDefinition { Equal = ["$getStatus.status", "Completed"] }]
        };

        var result = await JsonWorkflowRunner.RunAsync(withAssertion, contracts, targets);

        Assert.True(result.Passed);
        Assert.Empty(result.AssertionErrors);
    }
    [Fact]
    public async Task Poll_RetriesOnTransient503_ThenSucceeds()
    {
        ServeStatusSequence(
            (503, "\"Service Unavailable\""),
            (503, "\"Service Unavailable\""),
            (200, "{\"status\":\"Completed\"}"));

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            Until = new AssertionDefinition { Equal = ["$getStatus.status", "Completed"] },
            IntervalMs = 1,
            TimeoutMs = 5000
        });

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);

        Assert.True(result.Passed);
        Assert.Equal(3, _callCount[0]);
    }

    [Fact]
    public async Task Poll_RetriesOnTransient429_ThenSucceeds()
    {
        ServeStatusSequence(
            (429, "\"Too Many Requests\""),
            (200, "{\"status\":\"Done\"}"));

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            IntervalMs = 1,
            TimeoutMs = 5000
        });

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);

        Assert.True(result.Passed);
        Assert.Equal(2, _callCount[0]);
    }

    [Fact]
    public async Task Poll_FailsImmediatelyOnNonTransient400()
    {
        ServeStatusSequence(
            (400, "{\"error\":\"Bad Request\"}"));

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            Until = new AssertionDefinition { Equal = ["$getStatus.status", "Completed"] },
            IntervalMs = 1,
            TimeoutMs = 5000
        });

        var ex = await Assert.ThrowsAsync<JsonWorkflowException>(() =>
            JsonWorkflowRunner.RunAsync(workflow, contracts, targets));

        Assert.Contains("400", ex.Message);
        Assert.Equal(1, _callCount[0]);
    }

    [Fact]
    public async Task Poll_RetriesOnTransient404_ThenSucceeds()
    {
        ServeStatusSequence(
            (404, "{\"error\":\"Not Found\"}"),
            (404, "{\"error\":\"Not Found\"}"),
            (200, "{\"status\":\"Ready\"}"));

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            Until = new AssertionDefinition { Equal = ["$getStatus.status", "Ready"] },
            IntervalMs = 1,
            TimeoutMs = 5000
        });

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);

        Assert.True(result.Passed);
        Assert.Equal(3, _callCount[0]);
    }

    [Fact]
    public async Task Poll_TimesOutIfTransientErrorsPersist()
    {
        ServeStatusSequence(
            (503, "\"Service Unavailable\""));

        var (workflow, contracts, targets) = BuildWorkflow(new StepInvocation
        {
            Poll = "getStatus",
            Until = new AssertionDefinition { Equal = ["$getStatus.status", "Completed"] },
            IntervalMs = 10,
            TimeoutMs = 200
        });

        var ex = await Assert.ThrowsAsync<JsonWorkflowException>(() =>
            JsonWorkflowRunner.RunAsync(workflow, contracts, targets));

        Assert.Contains("timed out", ex.Message);
        Assert.True(_callCount[0] > 1);
    }
}

public class CaptureRequestAsTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;

    public CaptureRequestAsTests()
    {
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                var bytes = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"resp-1\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _listener.Stop();

    [Fact]
    public async Task CaptureRequestAs_StoresResolvedFields()
    {
        var contracts = new Dictionary<string, StepContractDefinition>
        {
            ["createItem"] = new()
            {
                Defaults = new()
                {
                    ["name"]  = new FieldValueDefinition { Static = JsonSerializer.SerializeToElement("Widget") },
                    ["price"] = new FieldValueDefinition { Static = JsonSerializer.SerializeToElement(9.99) }
                }
            }
        };
        var targets = new List<TargetDefinition>
        {
            new() { BaseUrl = $"http://127.0.0.1:{_port}", Steps = new() { ["createItem"] = new() { Method = "POST", Path = "/items" } } }
        };

        var workflow = new WorkflowDefinition(
            "test",
            [new StepInvocation { Step = "createItem", CaptureRequestAs = "itemRequest" }],
            [
                new AssertionDefinition { Equal = ["$itemRequest.name", "Widget"] },
                new AssertionDefinition { NotEmpty = "$itemRequest.price" }
            ]);

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);
        Assert.True(result.Passed, string.Join(", ", result.AssertionErrors));
    }

    [Fact]
    public async Task CaptureRequestAs_IsAvailableToSubsequentSteps()
    {
        var contracts = new Dictionary<string, StepContractDefinition>
        {
            ["createItem"] = new()
            {
                Defaults = new() { ["name"] = new FieldValueDefinition { Static = JsonSerializer.SerializeToElement("Widget") } }
            },
            ["createOrder"] = new()
            {
                Defaults = new() { ["itemName"] = new FieldValueDefinition { From = "itemRequest.name" } }
            }
        };
        var targets = new List<TargetDefinition>
        {
            new()
            {
                BaseUrl = $"http://127.0.0.1:{_port}",
                Steps = new()
                {
                    ["createItem"]  = new() { Method = "POST", Path = "/items" },
                    ["createOrder"] = new() { Method = "POST", Path = "/orders" }
                }
            }
        };

        var workflow = new WorkflowDefinition(
            "test",
            [
                new StepInvocation { Step = "createItem",  CaptureRequestAs = "itemRequest" },
                new StepInvocation { Step = "createOrder" }
            ],
            [new AssertionDefinition { Equal = ["$itemRequest.name", "Widget"] }]);

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);
        Assert.True(result.Passed, string.Join(", ", result.AssertionErrors));
    }

    [Fact]
    public async Task CaptureRequestAs_DoesNotAffectResponseCapture()
    {
        var contracts = new Dictionary<string, StepContractDefinition>
        {
            ["createItem"] = new()
            {
                Defaults = new() { ["name"] = new FieldValueDefinition { Static = JsonSerializer.SerializeToElement("Widget") } }
            }
        };
        var targets = new List<TargetDefinition>
        {
            new() { BaseUrl = $"http://127.0.0.1:{_port}", Steps = new() { ["createItem"] = new() { Method = "POST", Path = "/items" } } }
        };

        var workflow = new WorkflowDefinition(
            "test",
            [new StepInvocation { Step = "createItem", CaptureRequestAs = "itemRequest" }],
            [
                new AssertionDefinition { Equal = ["$createItem.id", "resp-1"] },
                new AssertionDefinition { Equal = ["$itemRequest.name", "Widget"] }
            ]);

        var result = await JsonWorkflowRunner.RunAsync(workflow, contracts, targets);
        Assert.True(result.Passed, string.Join(", ", result.AssertionErrors));
    }
}
