using System.Text.Json;
using Walkthrough.Core;
using Walkthrough.Http;
using Walkthrough.Json;
using static Walkthrough.Core.FieldValues;
using Xunit;
using System.Collections.Generic;

namespace Walkthrough.UnitTests;

public class FieldValueTests
{
    private readonly WorkflowContext _context = new();

    [Fact]
    public void Static_AlwaysReturnsGivenValue()
    {
        var field = Static("hello");
        Assert.Equal("hello", field.Resolve(_context));
        Assert.Equal("hello", field.Resolve(_context));
    }

    [Fact]
    public void Generated_InvokesLambdaEachTime()
    {
        var counter = 0;
        var field = Generated(() => ++counter);

        Assert.Equal(1, field.Resolve(_context));
        Assert.Equal(2, field.Resolve(_context));
    }
}

public class WorkflowContextTests
{
    private record FakeResponse();
    private record FakeRequest() : WorkflowRequest<FakeResponse, FakeRequest>, IWorkflowRequest
    {
        public static string StepName => "login";
    }
    private class FakeTarget : ITarget
    {
        public bool CanHandle(Type _) => true;
        public Task<TResponse> ExecuteAsync<TResponse>(WorkflowRequest<TResponse> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
            => Task.FromResult((TResponse)(object)new FakeResponse());
    }

    [Fact]
    public async Task Get_ThrowsDescriptiveException_WhenStepNotFound()
    {
        var context = new WorkflowContext();
        var runner  = new WorkflowRunner(context, _ => new FakeTarget());
        await runner.ExecuteAsync(new FakeRequest());

        var ex = Assert.Throws<WorkflowContextException>(
            () => context.Get<object>("missingStep"));

        Assert.Contains("missingStep", ex.Message);
        Assert.Contains("login", ex.Message);
    }

    [Fact]
    public void GetOrDefault_ReturnsNull_WhenStepNotExecuted()
    {
        var context = new WorkflowContext();
        Assert.Null(context.GetOrDefault<FakeResponse>("login"));
    }

    [Fact]
    public async Task GetOrDefault_ReturnsValue_AfterStepExecutes()
    {
        var context = new WorkflowContext();
        var runner  = new WorkflowRunner(context, _ => new FakeTarget());
        await runner.ExecuteAsync(new FakeRequest());
        Assert.NotNull(context.GetOrDefault<FakeResponse>("login"));
    }
}

public class GetOrDefaultInFromLambdaTests
{
    private record TokenResponse(string Token);
    private record LoginRequest() : WorkflowRequest<TokenResponse, LoginRequest>, IWorkflowRequest
    {
        public static string StepName => "login";
    }
    private record PayloadResponse(string Authorization);
    private record ApiRequest() : WorkflowRequest<PayloadResponse, ApiRequest>, IWorkflowRequest
    {
        public static string StepName => "api";
        public IFieldValue<string> Authorization { get; init; } =
            From(ctx => ctx.GetOrDefault<TokenResponse>("login") is { } login
                ? $"Bearer {login.Token}"
                : "");
    }

    private class FakeLogin(string token) : ITarget
    {
        public bool CanHandle(Type _) => true;
        public Task<T> ExecuteAsync<T>(WorkflowRequest<T> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
            => Task.FromResult((T)(object)new TokenResponse(token));
    }

    [Fact]
    public void GetOrDefault_Null_FromLambdaReturnsFallback()
    {
        var context  = new WorkflowContext();
        var resolved = FieldValueResolver.Resolve(new ApiRequest(), context);
        Assert.Equal("", resolved["Authorization"]);
    }

    [Fact]
    public async Task GetOrDefault_NotNull_FromLambdaReturnsValue()
    {
        var context = new WorkflowContext();
        var runner  = new WorkflowRunner(context, _ => new FakeLogin("tok-abc"));
        await runner.ExecuteAsync(new LoginRequest());

        var resolved = FieldValueResolver.Resolve(new ApiRequest(), context);
        Assert.Equal("Bearer tok-abc", resolved["Authorization"]);
    }
}

public class FieldValueResolverTests
{
    private record TestResponse;
    private class TestProtocol;

    private record TestRequest<TProtocol>() : WorkflowRequest<TestResponse, TestRequest<TProtocol>>, IWorkflowRequest
    {
        public static string StepName => "test";
        public IFieldValue<string> Name  { get; init; } = Static("Alice");
        public IFieldValue<int>    Count { get; init; } = Static(42);
    }

    [Fact]
    public void Resolve_ResolvesAllFieldValues()
    {
        var context = new WorkflowContext();
        var request = new TestRequest<TestProtocol>();

        var resolved = FieldValueResolver.Resolve<TestResponse>(request, context);

        Assert.Equal("Alice", resolved["Name"]);
        Assert.Equal(42, resolved["Count"]);
        Assert.False(resolved.ContainsKey("StepName"));
        Assert.False(resolved.ContainsKey("StepName"));
    }

    [Fact]
    public void Resolve_RespectsWithOverride()
    {
        var context = new WorkflowContext();
        var request = new TestRequest<TestProtocol>() with { Name = Static("Bob") };

        var resolved = FieldValueResolver.Resolve<TestResponse>(request, context);

        Assert.Equal("Bob", resolved["Name"]);
        Assert.Equal(42, resolved["Count"]);
    }
}

public class RecursiveFieldValueTests
{
    private record Inner
    {
        public IFieldValue<string> Value { get; init; } = Static("default");
    }

    private record Outer() : WorkflowRequest<object, Outer>, IWorkflowRequest
    {
        public static string StepName => "test";
        public IFieldValue<Inner> Nested { get; init; } = Static(new Inner());
    }

    private record DeepInner
    {
        public IFieldValue<string> Leaf { get; init; } = Static("deep");
    }

    private record MiddleLayer
    {
        public IFieldValue<DeepInner> Inner { get; init; } = Static(new DeepInner());
    }

    private record DeepOuter() : WorkflowRequest<object, DeepOuter>, IWorkflowRequest
    {
        public static string StepName => "test";
        public IFieldValue<MiddleLayer> Middle { get; init; } = Static(new MiddleLayer());
    }

    [Fact]
    public void NestedRecord_FieldValuesResolvedRecursively()
    {
        var context = new WorkflowContext();
        var resolved = FieldValueResolver.Resolve<object>(new Outer(), context);

        var nested = Assert.IsType<Dictionary<string, object?>>(resolved["Nested"]);
        Assert.Equal("default", nested["Value"]);
    }

    [Fact]
    public void NestedRecord_WithOverride_ResolvesCorrectly()
    {
        var context = new WorkflowContext();
        var resolved = FieldValueResolver.Resolve<object>(
            new Outer() with { Nested = Static(new Inner() with { Value = Static("overridden") }) },
            context);

        var nested = Assert.IsType<Dictionary<string, object?>>(resolved["Nested"]);
        Assert.Equal("overridden", nested["Value"]);
    }

    [Fact]
    public void DeeplyNestedRecord_AllLevelsResolved()
    {
        var context = new WorkflowContext();
        var resolved = FieldValueResolver.Resolve<object>(new DeepOuter(), context);

        var middle = Assert.IsType<Dictionary<string, object?>>(resolved["Middle"]);
        var inner  = Assert.IsType<Dictionary<string, object?>>(middle["Inner"]);
        Assert.Equal("deep", inner["Leaf"]);
    }
}

public class FromDefaultTests
{
    private static FieldValueDefinition StaticField(object value) =>
        new() { Static = JsonSerializer.SerializeToElement(value) };

    private static Dictionary<string, StepContractDefinition> StepDefs => new()
    {
        ["setUser"] = new() { AccumulateAs = "users", Defaults = new() { ["id"] = StaticField("user-123") } },
        ["addItem"] = new()
        {
            AccumulateAs = "items",
            Defaults = new()
            {
                ["ownerId"] = new FieldValueDefinition { From = "setUser.id", Default = StaticField("guest") }
            }
        }
    };

    [Fact]
    public async Task From_UsesDefault_WhenCaptureNotPresent()
    {
        var workflow = new WorkflowDefinition("test",
            [new StepInvocation { Build = "addItem" }],
            [new AssertionDefinition { Equal = ["$addItem.ownerId", "guest"] }]);

        var result = await JsonWorkflowRunner.RunAsync(workflow, StepDefs, []);
        Assert.True(result.Passed, string.Join(", ", result.AssertionErrors));
    }

    [Fact]
    public async Task From_ResolvesValue_WhenCapturePresent()
    {
        var workflow = new WorkflowDefinition("test",
            [
                new StepInvocation { Build = "setUser" },
                new StepInvocation { Build = "addItem" }
            ],
            [new AssertionDefinition { Equal = ["$addItem.ownerId", "user-123"] }]);

        var result = await JsonWorkflowRunner.RunAsync(workflow, StepDefs, []);
        Assert.True(result.Passed, string.Join(", ", result.AssertionErrors));
    }

    [Fact]
    public async Task From_Throws_WhenRootPresentButFieldAbsent()
    {
        // setUser ran but has no "nonexistent" field — this is a bug, not a missing step
        var stepDefs = new Dictionary<string, StepContractDefinition>
        {
            ["setUser"] = new() { AccumulateAs = "users", Defaults = new() { ["id"] = StaticField("user-123") } },
            ["addItem"] = new()
            {
                AccumulateAs = "items",
                Defaults = new()
                {
                    ["ownerId"] = new FieldValueDefinition { From = "setUser.nonexistent", Default = StaticField("guest") }
                }
            }
        };

        var workflow = new WorkflowDefinition("test",
            [
                new StepInvocation { Build = "setUser" },
                new StepInvocation { Build = "addItem" }
            ],
            null);

        await Assert.ThrowsAsync<JsonWorkflowException>(() =>
            JsonWorkflowRunner.RunAsync(workflow, stepDefs, []));
    }
}

public class BuildableRequestAccumulationTests
{
    private record FakeResponse;
    private record LineItemResponse(string Name, int Count);

    private record LineItem() : BuildableRequest<LineItemResponse>
    {
        public IFieldValue<string> Name  { get; init; } = Static("Widget");
        public IFieldValue<int>    Count { get; init; } = Static(1);
    }

    private record OrderRequest() : WorkflowRequest<FakeResponse, OrderRequest>, IWorkflowRequest
    {
        public static string StepName => "createOrder";
        public IFieldValue<List<object>> Items { get; init; } = From(ctx => ctx.GetAccumulated<LineItem>());
    }

    private static (WorkflowContext ctx, WorkflowRunner runner) Make()
    {
        var ctx = new WorkflowContext();
        return (ctx, new WorkflowRunner(ctx));
    }

    [Fact]
    public async Task BuildAsync_ReturnsResolvedResponse()
    {
        var (_, runner) = Make();
        var result = await runner.BuildAsync(new LineItem() with { Name = Static("Gadget"), Count = Static(3) });

        Assert.Equal("Gadget", result.Name);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task BuildAsync_CapturesUnderTypeName_AccessibleViaGet()
    {
        var (ctx, runner) = Make();
        await runner.BuildAsync(new LineItem() with { Name = Static("First") });
        await runner.BuildAsync(new LineItem() with { Name = Static("Last") });

        var captured = ctx.Get<LineItemResponse>("LineItem");

        Assert.Equal("Last", captured.Name);
    }

    [Fact]
    public async Task BuildAsync_CapturedResult_AccessibleFromSubsequentFromLambda()
    {
        var (ctx, runner) = Make();
        await runner.BuildAsync(new LineItem() with { Name = Static("Widget") });

        var resolved = FieldValueResolver.Resolve(
            new OrderRequest() with
            {
                Items = From(c => c.GetAccumulated<LineItem>())
            },
            ctx);

        Assert.Equal("Widget", ctx.Get<LineItemResponse>("LineItem").Name);
    }

    [Fact]
    public async Task GetAccumulated_ReturnsTypedResponses()
    {
        var (ctx, runner) = Make();
        await runner.BuildAsync(new LineItem());
        await runner.BuildAsync(new LineItem() with { Name = Static("Gadget"), Count = Static(3) });

        var items = ctx.GetAccumulated<LineItem>();

        Assert.Equal(2, items.Count);
        var first  = Assert.IsType<LineItemResponse>(items[0]);
        var second = Assert.IsType<LineItemResponse>(items[1]);
        Assert.Equal("Widget", first.Name);
        Assert.Equal(1,        first.Count);
        Assert.Equal("Gadget", second.Name);
        Assert.Equal(3,        second.Count);
    }

    [Fact]
    public async Task BuildableItems_AvailableInResolvedRequest()
    {
        var (ctx, runner) = Make();
        await runner.BuildAsync(new LineItem() with { Name = Static("Widget"), Count = Static(2) });
        await runner.BuildAsync(new LineItem() with { Name = Static("Gadget"), Count = Static(5) });

        var resolved = FieldValueResolver.Resolve(new OrderRequest(), ctx);

        var items = Assert.IsType<List<object?>>(resolved["Items"]);
        Assert.Equal(2, items.Count);
        var first  = Assert.IsType<LineItemResponse>(items[0]);
        var second = Assert.IsType<LineItemResponse>(items[1]);
        Assert.Equal("Widget", first.Name);
        Assert.Equal(2,        first.Count);
        Assert.Equal("Gadget", second.Name);
        Assert.Equal(5,        second.Count);
    }

    [Fact]
    public async Task StaticFactoryVariants_AccumulateUnderSameType()
    {
        var (ctx, runner) = Make();
        await runner.BuildAsync(new LineItem() with { Name = Static("Widget") });
        await runner.BuildAsync(new LineItem() with { Name = Static("Gadget") });

        var items = ctx.GetAccumulated<LineItem>();

        Assert.Equal(2, items.Count);
        Assert.Equal("Widget", Assert.IsType<LineItemResponse>(items[0]).Name);
        Assert.Equal("Gadget", Assert.IsType<LineItemResponse>(items[1]).Name);
    }

    [Fact]
    public async Task GetAccumulated_ClearsAccumulation_OnRead()
    {
        var (ctx, runner) = Make();
        await runner.BuildAsync(new LineItem() with { Name = Static("Widget") });

        var first  = ctx.GetAccumulated<LineItem>();
        var second = ctx.GetAccumulated<LineItem>();

        Assert.Single(first);
        Assert.Empty(second);
    }

    private record BaseItemResponse(string Kind);
    private abstract record BaseItem() : BuildableRequest<BaseItemResponse>
    {
        public override Type AccumulationKey => typeof(BaseItem);
    }
    private record AlphaItem() : BaseItem
    {
        public IFieldValue<string> Kind { get; init; } = Static("alpha");
    }
    private record BetaItem() : BaseItem
    {
        public IFieldValue<string> Kind { get; init; } = Static("beta");
    }

    [Fact]
    public async Task AccumulationKey_Override_PullsSubtypesIntoBaseTypeBucket()
    {
        var (ctx, runner) = Make();
        await runner.BuildAsync(new AlphaItem());
        await runner.BuildAsync(new BetaItem());

        var items = ctx.GetAccumulated<BaseItem>();

        Assert.Equal(2, items.Count);
        Assert.Equal("alpha", Assert.IsType<BaseItemResponse>(items[0]).Kind);
        Assert.Equal("beta",  Assert.IsType<BaseItemResponse>(items[1]).Kind);
    }
}

public class TemplateFieldValueTests
{
    private static FieldValueDefinition TemplateDef(string template) =>
        new() { Template = template };

    private static Dictionary<string, object?> LoginCapture(string token) => new()
    {
        ["login"] = new Dictionary<string, object?> { ["token"] = token }
    };

    [Fact]
    public void Template_SubstitutesCapturePath()
    {
        var result = JsonValueResolver.Resolve(TemplateDef("Bearer {login.token}"))
            .Resolve(LoginCapture("abc123"));

        Assert.Equal("Bearer abc123", result);
    }

    [Fact]
    public void Template_SubstitutesMultiplePlaceholders()
    {
        var captures = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["first"] = "Alice", ["last"] = "Smith" }
        };
        var result = JsonValueResolver.Resolve(TemplateDef("{user.first} {user.last}"))
            .Resolve(captures);

        Assert.Equal("Alice Smith", result);
    }

    [Fact]
    public void Template_NoPlaceholders_ReturnsLiteral()
    {
        var result = JsonValueResolver.Resolve(TemplateDef("plain text")).Resolve([]);

        Assert.Equal("plain text", result);
    }

    [Fact]
    public void Template_EscapedBraces_ArePreserved()
    {
        var result = JsonValueResolver.Resolve(TemplateDef("{{literal}}")).Resolve([]);

        Assert.Equal("{literal}", result);
    }

    [Fact]
    public void Template_MissingCapture_Throws()
    {
        Assert.Throws<JsonWorkflowException>(() =>
            JsonValueResolver.Resolve(TemplateDef("Bearer {login.token}")).Resolve([]));
    }

    [Fact]
    public async Task Template_UsedAsHeader_InBuildWorkflow()
    {
        var stepDefs = new Dictionary<string, StepContractDefinition>
        {
            ["setToken"] = new() { AccumulateAs = "tokens", Defaults = new() { ["token"] = StaticField("my-token") } },
            ["addItem"]  = new()
            {
                AccumulateAs = "items",
                Defaults = new()
                {
                    ["authHeader"] = new FieldValueDefinition { Template = "Bearer {setToken.token}" }
                }
            }
        };

        var workflow = new WorkflowDefinition("test",
            [
                new StepInvocation { Build = "setToken" },
                new StepInvocation { Build = "addItem" }
            ],
            [new AssertionDefinition { Equal = ["$addItem.authHeader", "Bearer my-token"] }]);

        var result = await JsonWorkflowRunner.RunAsync(workflow, stepDefs, []);
        Assert.True(result.Passed, string.Join(", ", result.AssertionErrors));
    }

    private static FieldValueDefinition StaticField(object value) =>
        new() { Static = JsonSerializer.SerializeToElement(value) };
}

public class MultiTargetWorkflowTests
{
    private record TokenResponse(string Token);
    private record UserResponse(string Id);

    private record LoginRequest() : WorkflowRequest<TokenResponse, LoginRequest>, IWorkflowRequest
    {
        public static string StepName => "login";
    }

    // Token field resolves from the login capture — produced by a different target.
    private record CreateUserRequest() : WorkflowRequest<UserResponse, CreateUserRequest>, IWorkflowRequest
    {
        public static string StepName => "createUser";
        public IFieldValue<string> Token { get; init; } = From(ctx => ctx.Get<TokenResponse>("login").Token);
    }

    private class CountingFakeTarget<TResponse>(TResponse response) : ITarget
    {
        public int CallCount { get; private set; }
        public bool CanHandle(Type _) => true;
        public Task<T> ExecuteAsync<T>(WorkflowRequest<T> request, Dictionary<string, object?> resolvedFields, WorkflowContext context)
        {
            CallCount++;
            return Task.FromResult((T)(object)response!);
        }
    }

    [Fact]
    public async Task EachStep_RoutedToCorrectTarget()
    {
        var loginTarget = new CountingFakeTarget<TokenResponse>(new TokenResponse("abc"));
        var userTarget  = new CountingFakeTarget<UserResponse>(new UserResponse("u1"));

        var context = new WorkflowContext();
        var runner  = new WorkflowRunner(context,
            n => n == "login" ? (ITarget)loginTarget : userTarget);

        await runner.ExecuteAsync(new LoginRequest());
        await runner.ExecuteAsync(new CreateUserRequest());

        Assert.Equal(1, loginTarget.CallCount);
        Assert.Equal(1, userTarget.CallCount);
    }

    [Fact]
    public async Task CapturesFromBothTargets_AreAccessible()
    {
        var loginTarget = new CountingFakeTarget<TokenResponse>(new TokenResponse("abc"));
        var userTarget  = new CountingFakeTarget<UserResponse>(new UserResponse("u1"));

        var context = new WorkflowContext();
        var runner  = new WorkflowRunner(context,
            n => n == "login" ? (ITarget)loginTarget : userTarget);

        await runner.ExecuteAsync(new LoginRequest());
        await runner.ExecuteAsync(new CreateUserRequest());

        Assert.Equal("abc", context.Get<TokenResponse>("login").Token);
        Assert.Equal("u1",  context.Get<UserResponse>("createUser").Id);
    }

    [Fact]
    public async Task From_ResolvesCapture_ProducedByDifferentTarget()
    {
        var loginTarget = new CountingFakeTarget<TokenResponse>(new TokenResponse("abc"));
        var userTarget  = new CountingFakeTarget<UserResponse>(new UserResponse("u1"));

        var context = new WorkflowContext();
        var runner  = new WorkflowRunner(context,
            n => n == "login" ? (ITarget)loginTarget : userTarget);

        await runner.ExecuteAsync(new LoginRequest());

        // Resolve the createUser request fields before executing — the Token From should
        // reach across the target boundary and read the login capture.
        var resolved = FieldValueResolver.Resolve(new CreateUserRequest(), context);

        Assert.Equal("abc", resolved["Token"]);
    }
}
