using Walkthrough.Core;
using Walkthrough.Http;
using static Walkthrough.Core.FieldValues;

namespace Walkthrough.SampleWorkflows;

public record UserResponse(string Id, string Email, string FirstName, string LastName, string Role);

public record CreateUserRequest() : WorkflowRequest<UserResponse>
{
    public IFieldValue<string> Email     { get; init; } = Generated(() => $"user-{Guid.NewGuid():N}@test.com");
    public IFieldValue<string> FirstName { get; init; } = Static("Test");
    public IFieldValue<string> LastName  { get; init; } = Static("User");
    public IFieldValue<string> Role      { get; init; } = Static("user");
}

public class CreateUserStep : HttpStep<CreateUserRequest, UserResponse, CreateUserStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Post;
    public static string     Path   => "/users";
}

// --- UpdateUserAddress ---

public record AddressRegionResponse(string State, string Country);
public record AddressInfoResponse(string Street, string City, AddressRegionResponse Region);
public record PrimaryContactResponse(AddressInfoResponse Address);
public record ContactInfoResponse(PrimaryContactResponse Primary);
public record UpdateUserAddressResponse(string UserId, ContactInfoResponse Contact);

public record RegionFields
{
    public IFieldValue<string> State   { get; init; } = Static("IL");
    public IFieldValue<string> Country { get; init; } = Static("US");
}

public record AddressFields
{
    public IFieldValue<string>       Street { get; init; } = Static("123 Main St");
    public IFieldValue<string>       City   { get; init; } = Static("Springfield");
    public IFieldValue<RegionFields> Region { get; init; } = Static(new RegionFields());
}

public record PrimaryFields
{
    public IFieldValue<AddressFields> Address { get; init; } = Static(new AddressFields());
}

public record ContactFields
{
    public IFieldValue<PrimaryFields> Primary { get; init; } = Static(new PrimaryFields());
}

public record UpdateUserAddressRequest() : WorkflowRequest<UpdateUserAddressResponse>
{
    public IFieldValue<string>        UserId  { get; init; } = From(ctx => ctx.Get<UserResponse>("CreateUserRequest").Id);
    public IFieldValue<ContactFields> Contact { get; init; } = Static(new ContactFields());
}

public class UpdateUserAddressStep : HttpStep<UpdateUserAddressRequest, UpdateUserAddressResponse, UpdateUserAddressStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Put;
    public static string     Path   => "/users/{userId}/address";
}

// --- GetUsersByRole ---

public record GetUsersByRoleRequest() : WorkflowRequest<List<UserResponse>>
{
    public IFieldValue<string> Role { get; init; } = Static("user");
}

public class GetUsersByRoleStep : HttpStep<GetUsersByRoleRequest, List<UserResponse>, GetUsersByRoleStep>, IHttpStep
{
    public static HttpMethod Method => HttpMethod.Get;
    public static string     Path   => "/users";

    public override Dictionary<string, string> MapQuery(Dictionary<string, object?> resolvedFields)
        => new() { ["role"] = resolvedFields["Role"]?.ToString() ?? "" };
}
