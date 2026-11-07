# Tenant-scoped project tools

`ProjectTools.cs` is an application-owned example compiled by the Core test suite. It
uses only Caravel Core and .NET types; it does not expose an endpoint or contact a model.
The tests supply a synthetic store with fictitious users, tenants, resources and grants.

At your authenticated application boundary, construct a `ProjectToolScope` with the
principal established by your authentication middleware and an `IProjectAccess` from
that application's scope. Pass only the tool name and JSON arguments to `InvokeAsync`:

```csharp
// trustedUser comes from authenticated application context, never from the request JSON.
var tools = new ProjectToolScope(trustedUser, scopedProjectAccess);
var arguments = JsonSerializer.SerializeToElement(new { resource = "project-1" });
ToolReply reply = await tools.InvokeAsync("read_project", arguments, cancellationToken);
```

The sample accepts a single authenticated identity with one subject and tenant claim.
It copies those claims at scope creation and accepts exactly one `resource` argument.
Extra fields, duplicate keys and malformed resource identifiers fail closed. Adapt that
binding to your authentication scheme explicitly; merely constructing a `ClaimsPrincipal`
does not authenticate anyone. A long-lived session still needs your application's normal
session-expiration and identity-revocation checks.

`IProjectAccess.AllowsAsync` must check current grants for the **subject, tenant, resource
and operation effect together**, including reads. A grant for `read_project` cannot grant
`archive_project`. Resource identifiers are untrusted: look them up inside the trusted
tenant, not globally followed by an unchecked return. The example returns minimal fake
results; select and validate real output fields for the caller before returning them.

Use a fresh scope for each request or unit of work. The registry retains its application
dependencies; it is not a lifetime manager. Concurrent scopes must not share mutable
caller context. The tests deliberately overlap two calls and verify both their outputs
and the separate stores reached by the handlers.

Permission is checked on every call, so removal of a grant blocks the next call. This
alone does not make authorization and a later write atomic: your `ExecuteAsync`
implementation must enforce current permission and eligibility in the same transaction
or concurrency boundary as its write when that guarantee is needed. The synthetic store
is a test double, not a transactional persistence implementation. Handle uncertain writes,
idempotency and retries in the application; an error does not imply that no effect occurred.

`InvokeAsync` formats fixed public errors without exception details or original arguments.
Cancellation still propagates. The underlying registry continues to propagate the exact
policy/handler exception, allowing the application to choose its own protected diagnostics.
Do not expose the registry directly across an untrusted transport.

Run the source example with:

```powershell
dotnet test tests/Caravel.Core.Tests -c Release --filter FullyQualifiedName~ApplicationPolicyTests
```

The existing `scripts/Test-ServicePackages.ps1` also compiles this same example and tests
against supplied Core package bytes in an isolated cache. See the [release gates](../../docs/NEXT-RELEASE.md)
for expected commit and artifact verification. These synthetic checks do not establish
an application's authentication, database transaction, session lifecycle or deployment acceptance.
