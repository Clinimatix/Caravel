using System.Security.Claims;
using System.Text.Json;
using Caravel.Fathom;

namespace FathomPolicyExample;

// Application-owned contracts, not additions to Caravel's public API.
internal interface IProjectAccess
{
    ValueTask<bool> AllowsAsync(string user, string tenant, string resource, ToolEffect effect,
        CancellationToken cancellationToken);
    // The implementation must recheck permission atomically with a write where required.
    ValueTask<JsonElement> ExecuteAsync(string user, string tenant, string resource, ToolEffect effect,
        CancellationToken cancellationToken);
}

internal sealed record ToolReply(bool Success, JsonElement? Value, string? Error);

// Construct once per authenticated application scope, from middleware's principal.
// Do not construct this principal from tool arguments or retain this scope as a singleton.
internal sealed class ProjectToolScope
{
    private readonly ClaimsPrincipal principal;
    internal ApplicationToolRegistry Registry { get; }

    internal ProjectToolScope(ClaimsPrincipal trustedPrincipal, IProjectAccess access)
    {
        // Snapshot claims so later mutation of the caller's principal cannot change this scope.
        principal = new ClaimsPrincipal(trustedPrincipal.Identities.Select(identity => new ClaimsIdentity(identity)));
        Registry = new ApplicationToolRegistry(
            [Register("read_project", ToolEffect.Read), Register("archive_project", ToolEffect.Write)],
            new ProjectPolicy(access));

        ApplicationTool Register(string name, ToolEffect effect) => new(new(name, effect),
            async (caller, arguments, token) =>
            {
                if (!ProjectPolicy.TryBind(caller, arguments, out var user, out var tenant, out var resource))
                    throw new UnauthorizedAccessException();
                return await access.ExecuteAsync(user, tenant, resource, effect, token);
            });
    }

    // The external invocation accepts a tool name and arguments, never a principal.
    internal async ValueTask<ToolReply> InvokeAsync(string name, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await Registry.ExecuteAsync(name, principal, arguments, cancellationToken);
            return new(true, value, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return new(false, null, "Operation unavailable."); }
        catch (KeyNotFoundException) { return new(false, null, "Operation unavailable."); }
        catch (ArgumentException) { return new(false, null, "Invalid request."); }
        catch (Exception)
        {
            // A real application can record a correlation ID in its protected diagnostics.
            // Never serialize exception messages, stack traces, or original arguments here.
            return new(false, null, "Operation failed.");
        }
    }
}

internal sealed class ProjectPolicy(IProjectAccess access) : IApplicationToolAuthorizer
{
    public ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ClaimsPrincipal principal,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var knownOperation = (tool.Name == "read_project" && tool.Effect == ToolEffect.Read) ||
            (tool.Name == "archive_project" && tool.Effect == ToolEffect.Write);
        if (!knownOperation || !TryBind(principal, arguments, out var user, out var tenant, out var resource))
            return ValueTask.FromResult(false);
        return access.AllowsAsync(user, tenant, resource, tool.Effect, cancellationToken);
    }

    internal static bool TryBind(ClaimsPrincipal principal, JsonElement arguments,
        out string user, out string tenant, out string resource)
    {
        user = tenant = resource = "";
        // This example accepts exactly one authenticated identity and one of each binding claim.
        var identities = principal.Identities.ToArray();
        if (identities.Length != 1 || !identities[0].IsAuthenticated) return false;
        var users = identities[0].FindAll(ClaimTypes.NameIdentifier).ToArray();
        var tenants = identities[0].FindAll("tenant").ToArray();
        if (users.Length != 1 || tenants.Length != 1 ||
            string.IsNullOrWhiteSpace(users[0].Value) || string.IsNullOrWhiteSpace(tenants[0].Value)) return false;
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        // A strict shape rejects supplied tenant/principal/effect overrides and duplicate keys.
        var properties = arguments.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != "resource" ||
            properties[0].Value.ValueKind != JsonValueKind.String) return false;
        resource = properties[0].Value.GetString()!;
        if (string.IsNullOrWhiteSpace(resource) || resource.Length > 64) return false;
        user = users[0].Value;
        tenant = tenants[0].Value;
        return true;
    }
}
