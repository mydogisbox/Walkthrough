using System.Collections.Specialized;
using System.Net;
using System.Text.Json;
using Walkthrough.Core;
using Walkthrough.Http;
using Walkthrough.Json;
using static Walkthrough.Core.FieldValues;
using Xunit;

namespace Walkthrough.UnitTests;

// ── JSON workflow runner headers ─────────────────────────────────────────────

public class JsonWorkflowRunnerHeaderTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly List<NameValueCollection> _receivedHeaders = [];

    public JsonWorkflowRunnerHeaderTests()
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

                _receivedHeaders.Add(ctx.Request.Headers);

                var bytes = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"1\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _listener.Stop();

    private static FieldValueDefinition F(string value) =>
        new() { Static = JsonSerializer.SerializeToElement(value) };

    private static Dictionary<string, StepContractDefinition> Contracts() => [];

    private List<TargetDefinition> Targets(
        Dictionary<string, FieldValueDefinition>? stepHeaders = null,
        Dictionary<string, FieldValueDefinition>? targetHeaders = null) =>
    [
        new TargetDefinition
        {
            BaseUrl = $"http://127.0.0.1:{_port}",
            Headers = targetHeaders,
            Steps = new() { ["doThing"] = new TargetStepDefinition { Method = "POST", Path = "/thing", Headers = stepHeaders } }
        }
    ];

    private static WorkflowDefinition Workflow(
        Dictionary<string, FieldValueDefinition>? invocationHeaders = null) =>
        new("test", [new StepInvocation { Step = "doThing", Headers = invocationHeaders }]);

    [Fact]
    public async Task TargetHeaders_SentWithRequest()
    {
        await JsonWorkflowRunner.RunAsync(Workflow(), Contracts(),
            Targets(targetHeaders: new() { ["X-Tenant-Id"] = F("acme") }));

        Assert.Equal("acme", _receivedHeaders[0]["X-Tenant-Id"]);
    }

    [Fact]
    public async Task StepHeaders_SentWithRequest()
    {
        await JsonWorkflowRunner.RunAsync(Workflow(), Contracts(),
            Targets(stepHeaders: new() { ["X-Api-Version"] = F("2") }));

        Assert.Equal("2", _receivedHeaders[0]["X-Api-Version"]);
    }

    [Fact]
    public async Task InvocationHeaders_SentWithRequest()
    {
        await JsonWorkflowRunner.RunAsync(
            Workflow(new() { ["X-Request-Id"] = F("req-123") }),
            Contracts(), Targets());

        Assert.Equal("req-123", _receivedHeaders[0]["X-Request-Id"]);
    }

    [Fact]
    public async Task StepWinsOverTarget_ForSameKey()
    {
        await JsonWorkflowRunner.RunAsync(Workflow(), Contracts(),
            Targets(
                stepHeaders:   new() { ["X-Level"] = F("step") },
                targetHeaders: new() { ["X-Level"] = F("target") }));

        Assert.Equal("step", _receivedHeaders[0]["X-Level"]);
    }

    [Fact]
    public async Task InvocationWinsOverStep_ForSameKey()
    {
        await JsonWorkflowRunner.RunAsync(
            Workflow(new() { ["X-Level"] = F("invocation") }),
            Contracts(), Targets(stepHeaders: new() { ["X-Level"] = F("step") }));

        Assert.Equal("invocation", _receivedHeaders[0]["X-Level"]);
    }

    [Fact]
    public async Task AllLevels_MergedTogether()
    {
        await JsonWorkflowRunner.RunAsync(
            Workflow(new() { ["X-Invocation"] = F("yes") }),
            Contracts(),
            Targets(
                stepHeaders:   new() { ["X-Step"]   = F("yes") },
                targetHeaders: new() { ["X-Target"] = F("yes") }));

        Assert.Equal("yes", _receivedHeaders[0]["X-Target"]);
        Assert.Equal("yes", _receivedHeaders[0]["X-Step"]);
        Assert.Equal("yes", _receivedHeaders[0]["X-Invocation"]);
    }
}

// ── C# HttpTarget headers ────────────────────────────────────────────────────

public class HttpTargetHeaderTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly List<NameValueCollection> _receivedHeaders = [];

    private record FakeResponse(string Id);

    private record WithHeadersRequest() : WorkflowRequest<FakeResponse>
    {
    }
    private class WithHeadersStep : HttpStep<WithHeadersRequest, FakeResponse, WithHeadersStep>, IHttpStep
    {
        public static HttpMethod Method => HttpMethod.Post;
        public static string Path => "/thing";
    }

    private record StepLevelHeaderRequest() : WorkflowRequest<FakeResponse>
    {
    }
    private class StepLevelHeaderStep : HttpStep<StepLevelHeaderRequest, FakeResponse, StepLevelHeaderStep>, IHttpStep
    {
        public static HttpMethod Method => HttpMethod.Post;
        public static string Path => "/thing";
        public override Dictionary<string, string> MapHeaders(Dictionary<string, object?> resolvedFields)
            => new() { ["X-Api-Version"] = "2" };
    }

    private record PerRequestHeaderRequest() : WorkflowRequest<FakeResponse>
    {
        public IFieldValue<string> XRequestId { get; init; } = Static("default");
    }
    private class PerRequestHeaderStep : HttpStep<PerRequestHeaderRequest, FakeResponse, PerRequestHeaderStep>, IHttpStep
    {
        public static HttpMethod Method => HttpMethod.Post;
        public static string Path => "/thing";
        public override Dictionary<string, string> MapHeaders(Dictionary<string, object?> resolvedFields)
            => new() { ["X-Request-Id"] = resolvedFields["XRequestId"]?.ToString() ?? "" };
    }

    private record StepVsTargetRequest() : WorkflowRequest<FakeResponse>
    {
    }
    private class StepVsTargetStep : HttpStep<StepVsTargetRequest, FakeResponse, StepVsTargetStep>, IHttpStep
    {
        public static HttpMethod Method => HttpMethod.Post;
        public static string Path => "/thing";
        public override Dictionary<string, string> MapHeaders(Dictionary<string, object?> resolvedFields)
            => new() { ["X-Level"] = "step" };
    }

    private record RequestFieldDrivesHeaderRequest() : WorkflowRequest<FakeResponse>
    {
        public IFieldValue<string> Level { get; init; } = Static("step-default");
    }
    private class RequestFieldDrivesHeaderStep : HttpStep<RequestFieldDrivesHeaderRequest, FakeResponse, RequestFieldDrivesHeaderStep>, IHttpStep
    {
        public static HttpMethod Method => HttpMethod.Post;
        public static string Path => "/thing";
        public override Dictionary<string, string> MapHeaders(Dictionary<string, object?> resolvedFields)
            => new() { ["X-Level"] = resolvedFields["Level"]?.ToString() ?? "" };
    }

    public HttpTargetHeaderTests()
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

                _receivedHeaders.Add(ctx.Request.Headers);

                var bytes = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"1\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _listener.Stop();

    private HttpTarget Target() => new HttpTarget($"http://127.0.0.1:{_port}");

    private WorkflowRunner Runner(HttpTarget target) =>
        new WorkflowRunner(new WorkflowContext(), target);

    [Fact]
    public async Task WithHeaders_SentWithRequest()
    {
        var target = Target()
            .Register<WithHeadersStep>()
            .WithHeaders(new Dictionary<string, IFieldValue<string>> { ["X-Tenant-Id"] = Static("acme") });
        await Runner(target).ExecuteAsync(new WithHeadersRequest());

        Assert.Equal("acme", _receivedHeaders[0]["X-Tenant-Id"]);
    }

    [Fact]
    public async Task StepHeaders_SentWithRequest()
    {
        await Runner(Target().Register<StepLevelHeaderStep>())
            .ExecuteAsync(new StepLevelHeaderRequest());

        Assert.Equal("2", _receivedHeaders[0]["X-Api-Version"]);
    }

    [Fact]
    public async Task RequestFieldDrivesHeader_SentWithRequest()
    {
        await Runner(Target().Register<PerRequestHeaderStep>())
            .ExecuteAsync(new PerRequestHeaderRequest() with { XRequestId = Static("req-123") });

        Assert.Equal("req-123", _receivedHeaders[0]["X-Request-Id"]);
    }

    [Fact]
    public async Task StepWinsOverTarget_ForSameKey()
    {
        var target = Target()
            .Register<StepVsTargetStep>()
            .WithHeaders(new Dictionary<string, IFieldValue<string>> { ["X-Level"] = Static("target") });
        await Runner(target).ExecuteAsync(new StepVsTargetRequest());

        Assert.Equal("step", _receivedHeaders[0]["X-Level"]);
    }

    [Fact]
    public async Task RequestFieldOverride_ChangesHeaderValue()
    {
        await Runner(Target().Register<RequestFieldDrivesHeaderStep>())
            .ExecuteAsync(new RequestFieldDrivesHeaderRequest() with { Level = Static("overridden") });

        Assert.Equal("overridden", _receivedHeaders[0]["X-Level"]);
    }

    // Auth expressed as a target-level header using From.
    // Uses a separate in-memory target for login so only one HTTP request hits the listener.
    private record FakeTokenResponse(string Token);
    private record FromAuthRequest() : WorkflowRequest<FakeResponse>
    {
    }
    private class FromAuthStep : HttpStep<FromAuthRequest, FakeResponse, FromAuthStep>, IHttpStep
    {
        public static HttpMethod Method => HttpMethod.Post;
        public static string Path => "/thing";
    }

    private record FakeLoginRequest() : WorkflowRequest<FakeTokenResponse>
    {
    }
    private class FakeLoginTarget(FakeTokenResponse response) : ITarget
    {
        public bool CanHandle(string _) => true;
        public Task<TResponse> ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
            => Task.FromResult((TResponse)(object)response);
    }

    [Fact]
    public async Task FromInHeader_ConstructsBearerToken()
    {
        var fakeLogin  = new FakeLoginTarget(new FakeTokenResponse("my-token"));
        var httpTarget = Target()
            .Register<FromAuthStep>()
            .WithHeaders(new Dictionary<string, IFieldValue<string>>
            {
                ["Authorization"] = From(ctx => $"Bearer {ctx.Get<FakeTokenResponse>(nameof(FakeLoginRequest)).Token}")
            });
        var runner = new WorkflowRunner(new WorkflowContext(), key =>
            key == nameof(FakeLoginRequest) ? (ITarget)fakeLogin : httpTarget);

        await runner.ExecuteAsync(new FakeLoginRequest());
        await runner.ExecuteAsync(new FromAuthRequest());

        Assert.Equal("Bearer my-token", _receivedHeaders[0]["Authorization"]);
    }
}
