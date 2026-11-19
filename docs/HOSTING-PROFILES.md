# Keep features consistent across deployment profiles

Changing from development credentials to managed identity should change the dependency
adapter, not silently remove application routes. Keep feature composition separate from
credential selection. An explicit read-only deployment is a different feature mode, not a
fallback when an edit dependency is misconfigured.

## Share registration and mapping

Use one application-owned `AddServiceFeatures` registration and one `MapServiceFeatures`
route mapper for every credential profile:

1. Validate the selected credential profile, explicit feature mode and required adapter
   settings before building the host. Reject unknown profiles and modes. Report missing
   setting names, never their values. Do not catch a registration failure and continue with
   fewer routes.
2. Select the appropriate adapter behind the same application interface. Keep application
   handlers, feature registration and route mapping shared. Managed identity selects how a
   dependency authenticates; it does not grant the calling user permission to edit.
3. Create one immutable feature decision during registration. Use that exact decision to
   map routes and report mounted capabilities. Do not build a second optimistic list from
   configuration in the capabilities handler.
4. Apply the same authentication and authorization policies in every deployment. Evaluate
   current user, tenant and resource permissions on each request. Mounted capabilities are
   deployment-level availability, not effective user permissions.

The [composition example](../tests/Caravel.AspNetCore.Tests/HostingProfileTests.cs) uses
`development` and `managed-identity` profiles with synthetic adapters and ASP.NET Core
TestServer. It demonstrates these combinations:

| Credential profile | Feature mode | Read route | Edit route | Authenticated capability response |
| --- | --- | --- | --- | --- |
| development | read-edit | mounted | mounted | read=true, edit=true |
| managed-identity | read-edit | mounted | mounted | read=true, edit=true |
| either | read-only | mounted | absent | read=true, edit=false |
| unknown or missing required setting | any | startup fails | startup fails | no response |

`MountedFeatures` is the shared decision. The example registers an adapter and that decision
once, then `MapServiceFeatures` maps the read route and conditionally the edit route. Its
authenticated capabilities route returns only the two booleans. A user without the edit
grant still receives a denial when the edit route is mounted. An explicitly read-only
host never mounts that route, even for a user with the edit grant.

This is a test example, not a production authentication or credential implementation.
Its synthetic authentication scheme accepts test headers; never register it in a deployed
application. Replace the synthetic adapter with your configured dependency adapter and use
your real authentication scheme and application policies. The example neither acquires a
cloud token nor validates provider access.

## Check the composition before deployment

Run the example with the repository's pinned SDK:

```sh
dotnet restore tests/Caravel.AspNetCore.Tests/Caravel.AspNetCore.Tests.csproj --locked-mode
dotnet test tests/Caravel.AspNetCore.Tests/Caravel.AspNetCore.Tests.csproj --no-restore --filter FullyQualifiedName~HostingProfileTests
```

In your own application, reuse the same focused cases with fake dependencies: both
credential profiles expose the intended read/edit routes, explicit read-only mode has no
edit route, invalid required configuration fails, capability output matches mapping, and
anonymous or insufficiently authorized callers remain denied. Include an authorized success
case so a policy that denies everyone cannot pass unnoticed. Test the actual production
composition method rather than a duplicate of its conditions.

A successful process-health response establishes neither workflow availability nor user
authority. Separately qualify dependency readiness, actual credential access and the intended
workflow in each deployed profile. If a required dependency becomes unavailable after
startup, report degraded readiness and safe operation failures; do not silently rewrite
mounted capabilities or grant permissions. Keep capability output free of credentials,
connection strings, provider endpoints and private infrastructure metadata.

See [hosting](HOSTING.md), [configuration](CONFIGURATION.md),
[authentication](AUTHENTICATION.md) and [observability](OBSERVABILITY.md).
