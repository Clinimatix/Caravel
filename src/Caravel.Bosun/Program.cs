using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Caravel.Bosun;

public static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public static async Task<int> Main(string[] args)
    {
        try { return await CreateCommand().Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(15) }); }
        catch (OperationCanceledException) { return 130; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static RootCommand CreateCommand()
    {
        var root = new RootCommand("Bosun — CLI for the Clinimatix Caravel framework");
        var name = new Argument<string>("name") { Description = "Application directory name (letters, digits, underscore; starts with a letter)." };
        var stack = new Option<string>("--stack") { Description = "Starter stack (razor or api).", DefaultValueFactory = _ => "razor" };
        var source = new Option<string?>("--framework-source") { Description = "Framework checkout root; use project references before package publication." };
        var create = new Command("new", "Create a Razor or API application in a new directory; does not restore packages.") { name, stack, source };
        create.SetAction(result =>
        {
            var path = CreateApplication(Directory.GetCurrentDirectory(), result.GetValue(name)!, result.GetValue(source), result.GetValue(stack)!);
            Console.WriteLine($"Created {path}\nNext: cd {result.GetValue(name)}\n      caravel serve");
            if (result.GetValue(source) is null) Console.WriteLine("The prerelease packages may not yet be published. Before publication, generate with --framework-source <framework-checkout> for buildable project references.");
        });
        root.Add(create);

        var project = new Option<string?>("--project") { Description = "Application .csproj or directory (defaults to the current directory)." };
        var watch = new Option<bool>("--watch") { Description = "Restart with dotnet watch on file changes." };
        var serve = new Command("serve", "Run the application using dotnet run and its launch profile.") { project, watch };
        serve.SetAction(async (result, token) =>
        {
            var arguments = new List<string>();
            if (result.GetValue(watch)) arguments.Add("watch");
            arguments.AddRange(["run", "--project", ResolveProject(result.GetValue(project))]);
            return (await RunAsync("dotnet", arguments, false, token)).ExitCode;
        });
        root.Add(serve);
        DevCommands.AddTo(root);

        var routeProject = new Option<string?>("--project") { Description = "Application .csproj or directory." };
        var routeJson = new Option<bool>("--json") { Description = "Output route metadata as JSON." };
        var routes = new Command("route:list", "Inspect endpoints via ExportCaravelRoutesAsync; executes application startup code without starting the host.") { routeProject, routeJson };
        routes.SetAction((result, token) => ListRoutesAsync(ResolveProject(result.GetValue(routeProject)), result.GetValue(routeJson), token));
        root.Add(routes);

        var doctor = new Command("doctor", "Read-only SDK and optional development dependency diagnostics.");
        doctor.SetAction((_, token) => DoctorAsync(token));
        root.Add(doctor);
        var version = new Command("version", "Print the installed Clinimatix Caravel Bosun version.");
        version.SetAction(_ => Console.WriteLine($"Clinimatix Caravel Bosun {Version}"));
        root.Add(version);
        var json = new Option<bool>("--json") { Description = "Output machine-readable JSON (the only schema format)." };
        var schema = new Command("schema", "Describe the implemented command surface as JSON.") { json };
        schema.SetAction(result => result.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            product = "Clinimatix Caravel",
            executable = "caravel",
            version = Version,
            commands = root.Subcommands.Select(command => new
            {
                name = command.Name,
                description = command.Description,
                arguments = command.Arguments.Select(argument => new
                {
                    name = argument.Name, description = argument.Description,
                    required = argument.Arity.MinimumNumberOfValues > 0 && !argument.HasDefaultValue,
                    type = argument.ValueType.FullName,
                    arity = new { min = argument.Arity.MinimumNumberOfValues, max = argument.Arity.MaximumNumberOfValues }
                }),
                options = command.Options.Select(option => new
                {
                    name = option.Name, description = option.Description, required = option.Required,
                    type = option.ValueType.FullName,
                    arity = new { min = option.Arity.MinimumNumberOfValues, max = option.Arity.MaximumNumberOfValues }
                }),
                effects = command.Name switch
                {
                    "new" => "Creates a new directory and source files; refuses existing targets.",
                    "serve" => "Builds and runs application code; may restore packages and open network listeners.",
                    "dev" => "Builds and watches application code, optionally runs one explicit worker, and stops owned process trees on exit; may restore packages and open network listeners.",
                    "route:list" => "Builds and executes application startup code to inspect endpoints; may restore packages. Does not start the host.",
                    _ => DataCommands.Effects(command.Name) ?? "Read-only diagnostics or metadata."
                }
            })
        }, JsonOptions)));
        root.Add(schema);
        DataCommands.AddTo(root);
        return root;
    }

    internal static string ResolveProject(string? value)
    {
        var path = Path.GetFullPath(value ?? Directory.GetCurrentDirectory());
        if (File.Exists(path) && Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) return path;
        if (!Directory.Exists(path)) throw new ArgumentException($"Application project not found: {path}");
        var projects = Directory.GetFiles(path, "*.csproj");
        if (projects.Length != 1) throw new ArgumentException("Specify --project with one application .csproj; the directory must contain exactly one project.");
        return projects[0];
    }

    internal static string CreateApplication(string parent, string name, string? frameworkSource, string stack = "razor")
    {
        if (stack is not ("razor" or "api")) throw new ArgumentException("Use --stack razor or --stack api.");
        if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_]{0,99}\\z", RegexOptions.CultureInvariant)
            || Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("Use 1–100 letters, digits, or underscores, starting with a letter; reserved Windows names are not allowed.");
        var target = Path.Combine(Path.GetFullPath(parent), name);
        if (Path.Exists(target)) throw new IOException($"Refusing to overwrite existing path: {target}");
        string reference;
        if (frameworkSource is not null)
        {
            var framework = Path.GetFullPath(Path.Combine(frameworkSource, "src", "Caravel.AspNetCore", "Caravel.AspNetCore.csproj"));
            if (!File.Exists(framework)) throw new ArgumentException("--framework-source must identify the framework checkout containing src/Caravel.AspNetCore/Caravel.AspNetCore.csproj.");
            reference = $"<ProjectReference Include=\"{SecurityElement.Escape(framework)}\" />";
        }
        else reference = $"<PackageReference Include=\"Clinimatix.Caravel.AspNetCore\" Version=\"{SecurityElement.Escape(Version)}\" />";

        // Stage in the same parent and rename atomically: an existing target is never merged or overwritten.
        var staging = Path.Combine(Path.GetFullPath(parent), $".caravel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            if (stack == "razor") Directory.CreateDirectory(Path.Combine(staging, "Pages"));
            Directory.CreateDirectory(Path.Combine(staging, "Properties"));
            File.WriteAllText(Path.Combine(staging, $"{name}.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    {{reference}}
                    {{(stack == "api" ? "<PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" Version=\"10.0.12\" />" : "")}}
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(staging, "Program.cs"), stack == "api" ? """
                using System.ComponentModel.DataAnnotations;
                using Caravel.AspNetCore;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddCaravel();
                builder.Services.AddValidation();
                builder.Services.AddOpenApi();
                builder.Services.AddHealthChecks();
                var app = builder.Build();
                app.UseHttpsRedirection();
                app.UseCaravel();
                app.MapHealthChecks("/health/live");
                app.Routes(routes => routes.Get("/hello", () => "Hello from Clinimatix Caravel").Name("hello"));
                // Native mapping lets .NET discover and validate the request type.
                app.MapPost("/api/greetings", (GreetingRequest request) => TypedResults.Ok(new { message = $"Hello, {request.Name}" }))
                    .WithName("greetings.create");
                if (app.Environment.IsDevelopment()) app.MapOpenApi();
                if (await app.ExportCaravelRoutesAsync(args)) return;
                await app.RunAsync();

                public sealed record GreetingRequest([property: Required, StringLength(100, MinimumLength = 2)] string Name);
                """ : """
                using Caravel.AspNetCore;

                var builder = WebApplication.CreateBuilder(args);
                builder.AddCaravel();
                builder.Services.AddValidation();
                builder.Services.AddRazorPages();
                var app = builder.Build();
                app.UseCaravel();
                app.UseHttpsRedirection();
                app.MapRazorPages();
                app.Routes(routes => routes.Get("/hello", () => "Hello from Clinimatix Caravel").Name("hello"));
                if (await app.ExportCaravelRoutesAsync(args)) return;
                await app.RunAsync();
                """);
            if (stack == "razor") File.WriteAllText(Path.Combine(staging, "Pages", "Index.cshtml"), $$"""
                @page
                <!doctype html>
                <html lang="en">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{{name}}</title></head>
                <body><main><h1>{{name}}</h1><p>Built with the Clinimatix Caravel framework.</p><a href="/hello">Try the named endpoint</a></main></body>
                </html>
                """);
            File.WriteAllText(Path.Combine(staging, "Properties", "launchSettings.json"), """
                {
                  "profiles": {
                    "https": {
                      "commandName": "Project",
                      "launchBrowser": false,
                      "applicationUrl": "https://localhost:7043;http://localhost:5043",
                      "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(staging, ".gitignore"), "bin/\nobj/\n.env\n.env.*\n!.env.example\n");
            File.WriteAllText(Path.Combine(staging, ".env.example"), $"APP_NAME={name}\n");
            File.WriteAllText(Path.Combine(staging, "README.md"), $"# {name}\n\nBuilt with the Clinimatix Caravel framework: expressive routing, integrated tooling, and native .NET foundations.\n\nRun `caravel serve`, `caravel route:list`, or `caravel doctor` from this directory. HTTPS requires a local development certificate; provision/trust one explicitly using the .NET SDK if needed. No secrets are generated or stored by this starter.\n\nLearn more in the [Clinimatix Caravel documentation](https://github.com/Clinimatix/Caravel#readme), including project status, licensing, and stewardship.\n");
            if (stack == "api") File.AppendAllText(Path.Combine(staging, "README.md"), "\n## Try the API\n\n- `GET /hello` returns a greeting.\n- `POST /api/greetings` accepts `{\"name\":\"Ada\"}`; missing or invalid names return validation ProblemDetails.\n- `GET /openapi/v1.json` describes the API in Development only.\n- `GET /health/live` checks that this process responds; it does not check dependencies or worker readiness.\n\nThese demonstration endpoints are public and store no data. Add the application's authentication, authorization, request limits and database configuration before using real data. Follow the framework's authentication and backend sample guides. No frontend toolchain or database is installed by this starter.\n");
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        return target;
    }

    private static async Task<int> ListRoutesAsync(string project, bool json, CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("caravel-routes-");
        var output = Path.Combine(directory.FullName, "routes.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await RunAsync("dotnet", ["run", "--project", project, "--no-launch-profile", "--", "--caravel-routes-json", output], true, timeout.Token);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine(result.Stdout + result.Stderr);
                return result.ExitCode;
            }
            if (!File.Exists(output)) throw new InvalidOperationException("The application did not export routes. Call `if (await app.ExportCaravelRoutesAsync(args)) return;` after mapping endpoints and before RunAsync().");
            var routes = JsonSerializer.Deserialize<RouteInfo[]>(await File.ReadAllTextAsync(output, token), JsonOptions)
                ?? throw new JsonException("The application returned no route metadata.");
            if (json) Console.WriteLine(JsonSerializer.Serialize(routes, JsonOptions));
            else
            {
                Console.WriteLine("METHOD  PATH  NAME  HANDLER");
                foreach (var route in routes) Console.WriteLine($"{(route.Methods.Count == 0 ? "*" : string.Join(',', route.Methods)),-6}  {route.Path}  {route.Name ?? "-"}  {route.Handler}");
            }
            return 0;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Route inspection timed out after 60 seconds. Ensure the application calls ExportCaravelRoutesAsync before RunAsync, and that restore/build can finish.");
        }
        finally { directory.Delete(recursive: true); }
    }

    internal static bool IsSupportedSdk(string? version) =>
        System.Version.TryParse(version?.Trim(), out var sdkVersion) && sdkVersion.Major == 10 && sdkVersion.Minor == 0 && sdkVersion.Build >= 100;

    private static async Task<int> DoctorAsync(CancellationToken token)
    {
        Console.WriteLine($"Clinimatix Caravel Doctor — Bosun {Version}");
        var sdk = await ProbeAsync("dotnet", ["--version"], token);
        var ready = IsSupportedSdk(sdk);
        Console.WriteLine($"{(ready ? "OK" : "FAIL")} .NET SDK 10: {sdk?.Trim() ?? "unavailable"}");
        await Optional("Git", "git", ["--version"]);
        await Optional("PowerShell", "pwsh", ["--version"]);
        await Optional("EF tooling (M1)", "dotnet", ["ef", "--version"]);
        await Optional("HTTPS development certificate (existence only; trust not checked)", "dotnet", ["dev-certs", "https", "--check"]);
        if (OperatingSystem.IsWindows()) await Optional("SQL Server LocalDB", "sqllocaldb", ["info"]);
        if (File.Exists("package.json"))
        {
            await Optional("Node", "node", ["--version"]);
            Console.WriteLine("OPTIONAL npm: run npm --version in your shell if this application uses frontend assets.");
        }
        if (File.Exists("compose.yml") || File.Exists("compose.yaml") || File.Exists("docker-compose.yml") || File.Exists("Dockerfile"))
            await Optional("Docker", "docker", ["--version"]);
        Console.WriteLine(ready ? "SDK check passed. Optional tools and production readiness are not acceptance checks." : "Select/install a stable .NET 10 SDK to use this framework baseline.");
        return ready ? 0 : 1;

        async Task Optional(string label, string executable, string[] arguments)
        {
            var result = await ProbeAsync(executable, arguments, token);
            Console.WriteLine($"OPTIONAL {label}: {(result is null ? "unavailable" : result.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "available")}");
        }
    }

    private static async Task<string?> ProbeAsync(string executable, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await RunAsync(executable, arguments, true, timeout.Token);
            return result.ExitCode == 0 ? result.Stdout : null;
        }
        catch (Win32Exception) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    internal static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, bool capture)
    {
        // SDK-launched tools should keep the selected SDK host even when test/build runners rewrite PATH.
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (executable == "dotnet" && !string.IsNullOrEmpty(dotnetHost)) executable = dotnetHost;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = capture, RedirectStandardError = capture };
        // Diagnostics must never trigger first-run certificate provisioning or PATH changes.
        info.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        info.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string executable, IEnumerable<string> arguments, bool capture, CancellationToken token, string? workingDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        var startInfo = StartInfo(executable, arguments, capture);
        if (workingDirectory is not null) startInfo.WorkingDirectory = workingDirectory;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = capture ? process.StandardOutput.ReadToEndAsync(token) : Task.FromResult("");
        var stderr = capture ? process.StandardError.ReadToEndAsync(token) : Task.FromResult("");
        try
        {
            await process.WaitForExitAsync(token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private sealed record RouteInfo(IReadOnlyList<string> Methods, string Path, string? Name, string Handler);
}
