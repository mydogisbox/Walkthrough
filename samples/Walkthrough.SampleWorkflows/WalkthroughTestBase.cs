using Walkthrough.Core;
using Walkthrough.Http;
using static Walkthrough.Core.FieldValues;

namespace Walkthrough.SampleWorkflows;

public abstract class WalkthroughTestBase
{
    private const string SampleApiUrl = "http://localhost:4200";
    private readonly WorkflowRunner _runner;

    protected WalkthroughTestBase()
    {
        var context = new WorkflowContext();

        var loginTarget = new HttpTarget(SampleApiUrl)
            .Register<LoginStep>();

        var apiTarget = new HttpTarget(SampleApiUrl)
            .Register<CreateUserStep>()
            .Register<UpdateUserAddressStep>()
            .Register<GetUsersByRoleStep>()
            .Register<CreateOrderStep>()
            .Register<GetOrderStep>()
            .Register<EchoHeadersStep>()
            .Register<EchoHeadersWithStepHeaderStep>()
            .WithHeaders(new Dictionary<string, IFieldValue<string>>
            {
                ["Authorization"] = From(ctx => $"Bearer {ctx.GetOrDefault<LoginResponse>("login")?.Token ?? ""}")
            });

        _runner = new WorkflowRunner(context, loginTarget, apiTarget);
    }

    protected Task<TResponse> ExecuteAsync<TResponse, TSelf>(WorkflowRequest<TResponse, TSelf> request)
        where TSelf : WorkflowRequest<TResponse, TSelf>, IWorkflowRequest
        => _runner.ExecuteAsync(request);

    protected Task<object> ExecuteRawAsync<TResponse, TSelf>(WorkflowRequest<TResponse, TSelf> request)
        where TSelf : WorkflowRequest<TResponse, TSelf>, IWorkflowRequest
        => _runner.ExecuteRawAsync(request);

    protected Task<TResponse> BuildAsync<TResponse>(BuildableRequest<TResponse> item)
        => _runner.BuildAsync(item);
}
