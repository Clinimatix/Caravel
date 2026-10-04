# Getting started with Clinimatix Caravel

This guide covers what happens inside a Caravel web app: how it starts up, how to organize services, and what the framework sets up for you. If you haven't created an app yet, start with the [quick start](../README.md#quick-start).

## Creating and running an app

This guide targets `26.1.0-rc2`. Install Bosun using a [configured package feed](RELEASE-POLICY.md#using-prerelease-packages) containing that exact version, or run the `caravel` tool from your selected source checkout with `dotnet run --project src/Caravel.Bosun -- <command>`. The table below uses source references; with the installed tool, omit `--framework-source .` to use packages from your configured feed.

| Command | What it does |
| --- | --- |
| `new MyApp --framework-source .` | Creates a Razor starter app in a new `MyApp` folder. It never overwrites an existing folder. `--framework-source` points the app at your local checkout instead of NuGet packages. |
| `new MyApi --stack api --framework-source .` | Creates an API starter with request validation, OpenAPI in Development, and a health endpoint. No Razor or frontend toolchain. |
| `new MyWorkspace --stack identity --framework-source .` | Creates an editable local Identity/workspace application with browser forms, versioned commands, receipts and background notices. Follow its README for explicit schema/account setup. |
| `serve --project MyApp` | Runs the app. Add `--watch` to restart when files change. |
| `dev --project MyApp` | Runs the app with file watching. Add `--worker <project>` to run a background worker alongside it. See [development sessions](DEVELOPMENT.md). |
| `route:list --project MyApp` | Lists every route, including ones mapped with plain ASP.NET Core (`MapPost`, Razor pages). Add `--json` for machine-readable output. |
| `doctor` | Checks your SDK and optional tools. It only reads; it never changes anything. |
| `schema --json` | Describes all commands, for tools and scripts. |

For scripts and tools, `schema --json` describes each argument and option: its .NET `type`, whether it's `required`, and its `arity` (how many values it takes). Each command also lists its `effects`, such as whether it runs your app's code or changes a database, so check those before automating it.

Razor/API starters listen on `https://localhost:7043` and `http://localhost:5043`; the Identity profile uses `https://localhost:7246`. Change `Properties/launchSettings.json` if you run several projects at once. The repository's basic sample app uses ports 7284 and 5284. HTTPS needs a .NET development certificate; if you don't have one, run `dotnet dev-certs https --trust`. Caravel never creates certificates for you.

The API starter comes with `GET /hello`, `POST /api/greetings` (with a validated `name`) and `GET /health/live`, plus an OpenAPI document at `/openapi/v1.json` in Development. These demo endpoints are public and store nothing. When you're ready to add accounts, ownership checks, request limits and a database, the [backend sample](BACKEND-SAMPLE.md) shows how the pieces fit together.

The optional `identity` stack is included in the `26.1.0-rc2` Bosun candidate; published RC1 does not include it. It bundles SQLite migrations, native provider configuration, current workspace authorization and the [authorized-command browser flow](AUTHORIZED-COMMANDS.md). Installed-tool generation works without a checkout. Its small UI supports password sign-in/sign-out; finished account/MFA screens and external identity remain separate integrations. No database, account, credential or cloud resource is created by `caravel new`.

## A typical `Program.cs`

```csharp
using Caravel.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddCaravel(options => options.AddProvider<AppServiceProvider>());

var app = builder.Build();
app.UseCaravel();

app.Routes(routes =>
{
    routes.Get("/", () => "Hello from Clinimatix Caravel").Name("home");
    routes.Group("/api", api => api.Post("/ping", () => "pong").Name("ping"));
});

if (await app.ExportCaravelRoutesAsync(args)) return;
await app.RunAsync();
```

- `AddCaravel` loads [configuration](CONFIGURATION.md) and registers your service providers.
- `UseCaravel` adds the standard middleware (see [HTTP defaults](#http-defaults)).
- `Routes` is Caravel's routing syntax. It produces ordinary ASP.NET Core endpoints, so you can mix it freely with `app.MapGet`, Razor pages, and anything else.
- `ExportCaravelRoutesAsync` is what makes `caravel route:list` work. Put it after your routes and before `RunAsync`.

## Service providers

A service provider groups related setup in one class: registering services, and running any async work the app needs before it starts taking requests.

```csharp
using Caravel.Core;

public sealed class AppServiceProvider : ServiceProvider
{
    public override void Register(IServiceCollection services)
    {
        services.AddSingleton<IGreeter, Greeter>();
    }

    public override ValueTask BootAsync(CaravelApplication app, CancellationToken cancellationToken)
    {
        // Warm caches, check connections, and so on.
        return ValueTask.CompletedTask;
    }
}
```

Both methods are optional; override only the ones you need.

If one provider needs another to go first, list it in `Dependencies`. Caravel orders providers for you and stops at startup if a dependency is missing, circular, or registered twice. Providers without dependencies run in the order you added them.

**When boot runs.** The app waits for every provider's `BootAsync` to finish before accepting requests. If a failure or cancellation happens during boot, the app doesn't start. If your *route mapping* depends on something a provider sets up, call `await app.UseCaravelAsync()` instead of `UseCaravel()` so boot finishes before you map routes.

**Scoped services.** `CaravelApplication.Services` is the root container. For scoped services such as a database context, create a scope first (`await using var scope = app.Services.CreateAsyncScope()`) rather than resolving them from the root.

## Validation

Turn on .NET's built-in validation in your app:

```csharp
builder.Services.AddValidation();
```

For request bodies you want validated, map the endpoint with plain ASP.NET Core (`app.MapPost`, and so on). .NET 10's validation generator can't always see types behind Caravel's `Routes` syntax. On positional records, target attributes at the property:

```csharp
app.MapPost("/api/greetings", (GreetingRequest request) => new { message = $"Hello, {request.Name}" });

public sealed record GreetingRequest([property: Required, StringLength(100, MinimumLength = 2)] string Name);
```

The [sample app](../samples/Caravel.App/Program.cs) shows this working end to end.

## HTTP defaults

`UseCaravel` sets up:

- Consistent error responses using ASP.NET Core's ProblemDetails and status pages
- A production exception handler and HSTS
- Authorization and antiforgery middleware
- `X-Content-Type-Options: nosniff` on responses

The starter app also adds HTTPS redirection, and the sample publishes an OpenAPI document in Development only.

These are sensible defaults, not a complete security setup. JSON APIs still need authentication, authorization, CORS, and rate-limiting policies that fit your application.

## Things to know about route inspection

`caravel route:list` runs your `Program.cs` up to the export line, then stops. It doesn't start the web server or run provider boot. Your registration code does run, though, so avoid side effects there (like sending email or writing to a database). If you call `UseCaravelAsync()` before the export line, provider boot runs too.
