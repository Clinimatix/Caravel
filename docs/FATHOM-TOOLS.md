# Caravel Fathom application tools

Applications can explicitly register a small set of in-process operations and authorize
every invocation. These development APIs live in `Caravel.Fathom` in Core and require no
model, provider credentials, web host or database. Constructing a registry is opt-in;
Caravel does not scan assemblies or register application operations automatically.

```csharp
using System.Security.Claims;
using System.Text.Json;
using Caravel.Fathom;

// Call from an authenticated application scope with its trusted caller context.
static async Task<JsonElement> ReadStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
{
    var registry = new ApplicationToolRegistry(
        [new ApplicationTool(new ToolDescriptor("status", ToolEffect.Read),
            (principal, arguments, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { status = "ready" }));
            })],
        new StatusPolicy());

    // Resolve the principal from the application's authenticated context.
    using var arguments = JsonDocument.Parse("{}");
    return await registry.ExecuteAsync("status", principal, arguments.RootElement, cancellationToken);
}

sealed class StatusPolicy : IApplicationToolAuthorizer
{
    public ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ClaimsPrincipal principal,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(tool.Name == "status" &&
            principal.Identity?.IsAuthenticated == true && principal.IsInRole("Operator"));
    }
}
```

`Descriptors` inspects names and declared read/write effects without invoking policies or
handlers. Names are case-sensitive and limited to 64 ASCII letters, digits, underscores
or hyphens. Duplicate names fail at construction. Treat this catalog as internal metadata;
discovery visibility and authentication at a future transport remain application concerns.

Execution accepts only JSON objects, snapshots arguments before asynchronous authorization,
and calls the required authorizer on every invocation, including reads. Authorization must
check current caller, tenant, resource and effect permissions using trusted application
state. A caller-supplied principal or read annotation is not proof of permission. Handlers
must validate their own argument shape, values and resource eligibility before acting.

Cancellation is checked before dispatch and after authorization and passed to both callbacks.
Handlers must cooperate with cancellation. Handler and policy errors propagate to the caller;
there is no automatic retry, logging, exception serialization or rollback. Applications own
safe error presentation, output minimization, input/output size limits, transactions,
idempotency and reconciliation of uncertain writes. A failed or canceled call can already
have performed effects. Read/write metadata documents intent; it cannot sandbox arbitrary
application code. Captured handler dependencies retain application-owned lifetimes; create a
registry within the appropriate scope when handlers use scoped services.

Fake policies and delegates can exercise success, denied reads/writes, cancellation and
errors without any external service. No network MCP endpoint, protocol compatibility,
provider adapter or model workflow is supplied by this registry. The required first-stable
foundation and later qualification remain in the [release plan](NEXT-RELEASE.md).

[Laravel MCP](https://laravel.com/framework/docs/13.x/mcp) provides useful reference
conventions for explicit tools, authorization and annotations. Its
[AI SDK](https://laravel.com/framework/docs/13.x/ai-sdk) documents model workflows and
fakes; those are separate from this in-process registry.
