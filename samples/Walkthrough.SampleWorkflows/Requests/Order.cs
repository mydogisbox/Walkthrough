using Walkthrough.Core;
using Walkthrough.Http;
using static Walkthrough.Core.FieldValues;

namespace Walkthrough.SampleWorkflows;

public record OrderItemResponse(string ProductName, int Quantity, decimal UnitPrice);
public record OrderResponse(string Id, string UserId, List<OrderItemResponse> Items, string Status);

public record AddOrderItemResponse(string ProductName, int Quantity, decimal UnitPrice);

public record AddOrderItem() : BuildableRequest<AddOrderItemResponse>
{
    public IFieldValue<string>  ProductName { get; init; } = Static("Widget");
    public IFieldValue<int>     Quantity    { get; init; } = Static(1);
    public IFieldValue<decimal> UnitPrice   { get; init; } = Static(9.99m);
}

public record CreateOrderRequest() : WorkflowRequest<OrderResponse>
{
    public IFieldValue<string>       UserId { get; init; } = From(ctx => ctx.Get<UserResponse>("CreateUserRequest").Id);
    public IFieldValue<List<object>> Items  { get; init; } = From(ctx => ctx.GetAccumulated<AddOrderItem>());
}

public class CreateOrderStep : HttpStep<CreateOrderRequest, OrderResponse, CreateOrderStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/orders";
}

public record GetOrderRequest() : WorkflowRequest<OrderResponse>
{
    public IFieldValue<string> OrderId { get; init; } = From(ctx => ctx.Get<OrderResponse>("CreateOrderRequest").Id);
}

public class GetOrderStep : HttpStep<GetOrderRequest, OrderResponse, GetOrderStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Get;
    public static string     Path   => "/orders/{orderId}";
}

// Polymorphic order items — shared fields live on the base; each subtype adds its own unique field.
// All variants accumulate under OrderLineItem because AccumulationKey is overridden on the base.
// Use GetAccumulated<OrderLineItem>() to retrieve all variants in insertion order.
public abstract record OrderLineItem() : BuildableRequest<AddOrderItemResponse>
{
    public override Type AccumulationKey => typeof(OrderLineItem);
    public IFieldValue<string>  ProductName { get; init; } = Static("Item");
    public IFieldValue<int>     Quantity    { get; init; } = Static(1);
    public IFieldValue<decimal> UnitPrice   { get; init; } = Static(9.99m);
}

// ShippingAddress is only meaningful for physical goods — not present on DigitalItem.
public record PhysicalItem() : OrderLineItem
{
    public IFieldValue<string> ShippingAddress { get; init; } = Static("123 Main St");
}

// DownloadUrl is only meaningful for digital goods — not present on PhysicalItem.
public record DigitalItem() : OrderLineItem
{
    public IFieldValue<string> DownloadUrl { get; init; } = Static("https://example.com/ebook");
}

// Marker type used as AccumulationKey — lets PhysicalLineItem and DigitalLineItem
// share a bucket despite resolving to different TResponse types.
public abstract record LineItem() : BuildableRequest;

public record PhysicalLineItemResponse(string ProductName, int Quantity, decimal UnitPrice, string ShippingAddress);
public record DigitalLineItemResponse(string ProductName, int Quantity, decimal UnitPrice, string DownloadUrl);

public record PhysicalLineItem() : BuildableRequest<PhysicalLineItemResponse>
{
    public override Type AccumulationKey => typeof(LineItem);
    public IFieldValue<string>  ProductName     { get; init; } = Static("Widget");
    public IFieldValue<int>     Quantity        { get; init; } = Static(1);
    public IFieldValue<decimal> UnitPrice       { get; init; } = Static(9.99m);
    public IFieldValue<string>  ShippingAddress { get; init; } = Static("123 Main St");
}

public record DigitalLineItem() : BuildableRequest<DigitalLineItemResponse>
{
    public override Type AccumulationKey => typeof(LineItem);
    public IFieldValue<string>  ProductName { get; init; } = Static("E-Book");
    public IFieldValue<int>     Quantity    { get; init; } = Static(1);
    public IFieldValue<decimal> UnitPrice   { get; init; } = Static(9.99m);
    public IFieldValue<string>  DownloadUrl { get; init; } = Static("https://example.com/ebook");
}

public record TypeMappedOrderRequest() : WorkflowRequest<OrderResponse>
{
    public IFieldValue<string>       UserId { get; init; } = From(ctx => ctx.Get<UserResponse>("CreateUserRequest").Id);
    public IFieldValue<List<object>> Items  { get; init; } = From(ctx => ctx.GetAccumulated<LineItem>());
}

// MapBody pattern-matches on the concrete response type to include type-specific fields.
public class TypeMappedOrderStep : HttpStep<TypeMappedOrderRequest, OrderResponse, TypeMappedOrderStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/orders";

    public override Dictionary<string, object?> MapBody(Dictionary<string, object?> resolvedFields)
    {
        var items  = (List<object?>)resolvedFields["Items"]!;
        var mapped = items.Select<object?, object?>(item => item switch
        {
            PhysicalLineItemResponse p => new Dictionary<string, object?>
            {
                ["productName"]     = p.ProductName,
                ["quantity"]        = p.Quantity,
                ["unitPrice"]       = p.UnitPrice,
                ["shippingAddress"] = p.ShippingAddress,
            },
            DigitalLineItemResponse d => new Dictionary<string, object?>
            {
                ["productName"] = d.ProductName,
                ["quantity"]    = d.Quantity,
                ["unitPrice"]   = d.UnitPrice,
                ["downloadUrl"] = d.DownloadUrl,
            },
            _ => item
        }).ToList();

        return new()
        {
            ["UserId"] = resolvedFields["UserId"],
            ["Items"]  = mapped,
        };
    }
}
