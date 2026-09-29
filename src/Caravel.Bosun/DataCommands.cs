using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Caravel.Bosun;

internal static class DataCommands
{
    internal static string? Effects(string name) => name switch
    {
        "make:model" or "make:seeder" or "make:factory" or "make:job" or "make:event" or "make:listener" => "Writes a new source file; refuses overwrite. Does not execute application code.",
        "make:migration" => "Builds and executes application design-time code; writes EF migration and snapshot source files.",
        "migrate:status" => "Builds and executes application design-time code; reads database migration history unless --no-connect is selected.",
        "migrate" => "Builds and executes application design-time code; applies migrations and changes the configured database.",
        "migrate:rollback" => "Requires --force and an explicit target; executes migration Down operations that can permanently erase data.",
        "migrate:fresh" => "Requires --force; permanently drops the configured database, then applies migrations. Failure after drop cannot recover deleted data.",
        "db:seed" => "Builds and executes application startup and registered seeders; writes to the configured database. Seeders own idempotency and transactions.",
        _ => null
    };

    internal static void AddTo(RootCommand root)
    {
        AddGenerator("model", "App/Models", "App.Models");
        AddGenerator("seeder", "Database/Seeders", "App.Database.Seeders");
        AddGenerator("factory", "Database/Factories", "App.Database.Factories");
        AddGenerator("job", "App/Jobs", "App.Jobs");
        AddGenerator("event", "App/Events", "App.Events");
        AddGenerator("listener", "App/Listeners", "App.Listeners");
        foreach (var (name, description) in new[]
        {
            ("make:migration", "Create an EF migration under Database/Migrations."),
            ("migrate", "Apply all pending EF migrations to the configured database."),
            ("migrate:status", "List EF migrations and their applied status."),
            ("migrate:rollback", "Migrate to an explicit earlier target; permanently destructive; requires --force."),
            ("migrate:fresh", "Drop the configured database and apply migrations; permanently destructive; requires --force.")
        })
        {
            var project = new Option<string?>("--project") { Description = "Migration project .csproj or directory." };
            var startup = new Option<string?>("--startup-project") { Description = "Startup .csproj or directory; defaults to --project." };
            var context = new Option<string?>("--context") { Description = "DbContext type name; required when EF finds multiple contexts." };
            var command = new Command(name, description) { project, startup, context };
            var migration = new Argument<string>(name == "migrate:rollback" ? "target" : "name") { Description = "Migration name/ID; rollback also accepts 0 (before the first migration)." };
            var force = new Option<bool>("--force") { Description = "Explicitly authorize permanent data loss in the configured database." };
            var json = new Option<bool>("--json") { Description = "Use EF tooling's native JSON output." };
            var noConnect = new Option<bool>("--no-connect") { Description = "List migration source without connecting to the database." };
            if (name is "make:migration" or "migrate:rollback") command.Add(migration);
            if (name is "migrate:rollback" or "migrate:fresh") command.Add(force);
            if (name == "migrate:status") { command.Add(json); command.Add(noConnect); }
            command.SetAction(async (result, token) =>
            {
                // Refuse before resolving projects, inspecting manifests, building, or spawning anything.
                var authorized = name is not ("migrate:rollback" or "migrate:fresh") || result.GetValue(force);
                if (!authorized) throw new ArgumentException($"{name} permanently changes/deletes database data. Re-run with --force only after verifying the configured database and your recovery plan.");
                var targetProject = Program.ResolveProject(result.GetValue(project));
                var startupProject = Program.ResolveProject(result.GetValue(startup) ?? targetProject);
                var directory = Path.GetDirectoryName(startupProject)!;
                RequireLocalEfTool(directory);
                if (name == "make:migration") EnsureSafeDirectory(Path.GetDirectoryName(targetProject)!, "Database/Migrations");
                var steps = PlanEf(name, targetProject, startupProject, result.GetValue(context),
                    name is "make:migration" or "migrate:rollback" ? result.GetValue(migration) : null,
                    authorized, name == "migrate:status" && result.GetValue(json), name == "migrate:status" && result.GetValue(noConnect));
                foreach (var arguments in steps)
                {
                    var exit = (await Program.RunAsync("dotnet", arguments, false, token, directory)).ExitCode;
                    if (exit != 0) return exit;
                }
                return 0;
            });
            root.Add(command);
        }

        var seedProject = new Option<string?>("--project") { Description = "Application .csproj or directory with RunCaravelSeedersAsync(args) hook." };
        var seeder = new Option<string?>("--seeder") { Description = "Registered seeder simple or fully qualified name; default runs all in registration order." };
        var timeout = new Option<int>("--timeout") { Description = "Maximum seconds before canceling the application (1–86400).", DefaultValueFactory = _ => 300 };
        var seed = new Command("db:seed", "Run the application's explicitly registered Clarion database seeders.") { seedProject, seeder, timeout };
        seed.SetAction((result, token) => SeedAsync(Program.ResolveProject(result.GetValue(seedProject)), result.GetValue(seeder), result.GetValue(timeout), token));
        root.Add(seed);

        void AddGenerator(string kind, string folder, string defaultNamespace)
        {
            var name = new Argument<string>("name") { Description = "PascalCase class name (letters, digits, underscore)." };
            var project = new Option<string?>("--project") { Description = "Application .csproj or directory." };
            var ns = new Option<string>("--namespace") { Description = "PascalCase dotted C# namespace.", DefaultValueFactory = _ => defaultNamespace };
            var command = new Command($"make:{kind}", $"Create a {kind} source file without overwriting existing files.") { name, project, ns };
            var model = new Option<string>("--model") { Description = "Fully qualified model type, such as App.Models.User.", Required = true };
            var eventType = new Option<string>("--event") { Description = "Fully qualified event type, such as App.Events.ReportReady.", Required = true };
            if (kind == "factory") command.Add(model);
            if (kind == "listener") command.Add(eventType);
            command.SetAction(result => Console.WriteLine($"Created {Generate(kind, Program.ResolveProject(result.GetValue(project)), result.GetValue(name)!, result.GetValue(ns)!, folder, kind == "factory" ? result.GetValue(model) : kind == "listener" ? result.GetValue(eventType) : null)}"));
            root.Add(command);
        }
    }

    internal static IReadOnlyList<string[]> PlanEf(string command, string project, string startup, string? context, string? migration, bool force, bool json = false, bool noConnect = false)
    {
        if (command is "migrate:rollback" or "migrate:fresh" && !force) throw new ArgumentException("Destructive migrations require --force.");
        if (command is "make:migration" or "migrate:rollback")
        {
            if (migration is null || !Regex.IsMatch(migration, command == "make:migration" ? "^[A-Z][A-Za-z0-9_]{0,99}\\z" : "^(0|[A-Za-z0-9_]{1,200})\\z"))
                throw new ArgumentException("Specify a valid migration name/ID; rollback requires an explicit target (or 0).");
        }
        if (context is not null && !Regex.IsMatch(context, "^[A-Za-z_][A-Za-z0-9_.]*\\z")) throw new ArgumentException("Invalid DbContext type name.");
        string[] common = context is null ? ["--project", project, "--startup-project", startup] : ["--project", project, "--startup-project", startup, "--context", context];
        string[] Ef(params string[] args) => ["tool", "run", "dotnet-ef", "--", .. args, .. common];
        return command switch
        {
            "make:migration" => [Ef("migrations", "add", migration!, "--output-dir", "Database/Migrations")],
            "migrate" => [Ef("database", "update")],
            "migrate:status" => [Ef(["migrations", "list", .. (json ? new[] { "--json" } : []), .. (noConnect ? new[] { "--no-connect" } : [])])],
            "migrate:rollback" => [Ef("database", "update", migration!)],
            // Preflight builds and validates the context/migration assembly before any deletion.
            "migrate:fresh" => [Ef("migrations", "list", "--no-connect"), Ef("database", "drop", "--force", "--no-build"), Ef("database", "update", "--no-build")],
            _ => throw new ArgumentException("Unknown EF command.")
        };
    }

    internal static string RequireLocalEfTool(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var manifest = Path.Combine(current.FullName, ".config", "dotnet-tools.json");
            if (!File.Exists(manifest)) continue;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.TryGetProperty("tools", out var tools) && tools.TryGetProperty("dotnet-ef", out var ef))
            {
                var version = ef.GetProperty("version").GetString();
                if (version is null || !Regex.IsMatch(version, "^10\\.0\\.[0-9]+\\z") || !System.Version.TryParse(version, out _))
                    throw new InvalidOperationException("Pin a stable, exact dotnet-ef 10.0.x version in .config/dotnet-tools.json.");
                return manifest;
            }
            if (document.RootElement.TryGetProperty("isRoot", out var root) && root.GetBoolean()) break;
        }
        throw new InvalidOperationException("A local .config/dotnet-tools.json must pin dotnet-ef 10.0.x. Install/restore it explicitly; Bosun never installs tools or falls back to a global EF tool.");
    }

    internal static string Generate(string kind, string project, string name, string ns, string folder, string? model = null)
    {
        if (!Regex.IsMatch(name, "^[A-Z][A-Za-z0-9_]{0,99}\\z") || Regex.IsMatch(name, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\\z", RegexOptions.IgnoreCase))
            throw new ArgumentException("Use a PascalCase class name of 1–100 letters, digits, or underscores; Windows device names are reserved.");
        if (!Regex.IsMatch(ns, "^[A-Z][A-Za-z0-9_]*(\\.[A-Z][A-Za-z0-9_]*)*\\z")) throw new ArgumentException("Use a PascalCase dotted namespace, such as App.Models.");
        if (kind == "factory" && (model is null || !Regex.IsMatch(model, "^[A-Z][A-Za-z0-9_]*(\\.[A-Z][A-Za-z0-9_]*)*\\z"))) throw new ArgumentException("Use a PascalCase dotted model type name.");
        if (kind == "listener" && (model is null || !Regex.IsMatch(model, "^[A-Z][A-Za-z0-9_]*(\\.[A-Z][A-Za-z0-9_]*)+\\z"))) throw new ArgumentException("Use a fully qualified PascalCase event type, such as App.Events.ReportReady.");
        var root = Path.GetDirectoryName(project)!;
        var directory = EnsureSafeDirectory(root, folder);
        var body = kind switch
        {
            "model" => $$"""
                using Caravel.Clarion;

                namespace {{ns}};

                [ClarionModel]
                public sealed class {{name}}
                {
                    public int Id { get; set; }
                }
                """,
            "seeder" => $$"""
                using Caravel.Clarion;

                namespace {{ns}};

                public sealed class {{name}} : IClarionSeeder
                {
                    public Task SeedAsync(IClarion db, CancellationToken cancellationToken)
                    {
                        throw new NotImplementedException("Implement idempotent seed data, then register this seeder with AddClarionSeeder<{{name}}>().");
                    }
                }
                """,
            "factory" => $$"""
                using Caravel.Clarion;

                namespace {{ns}};

                public static class {{name}}
                {
                    public static ModelFactory<{{model}}> Create() => new(index => new {{model}}());
                }
                """,
            "job" => $$"""
                namespace {{ns}};

                // Add the serializable data this job needs; identity comes from the trusted JobContext.
                public sealed record {{name}};

                // After implementation, register with AddQueueJob<{{name}}, {{name}}Handler>("your.stable.wire.name.v1").
                public sealed class {{name}}Handler : global::Caravel.Queues.IJobHandler<{{name}}>
                {
                    public global::System.Threading.Tasks.Task HandleAsync({{name}} job, global::Caravel.Queues.JobContext context,
                        global::System.Threading.CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new global::System.NotImplementedException("Implement idempotent job handling before registering this handler.");
                    }
                }
                """,
            "event" => $$"""
                namespace {{ns}};

                // Add the data your in-process listeners need.
                public sealed record {{name}};
                """,
            "listener" => $$"""
                namespace {{ns}};

                // After implementation, register with AddEventListener<global::{{model}}, {{name}}>().
                public sealed class {{name}} : global::Caravel.Events.IEventListener<global::{{model}}>
                {
                    public global::System.Threading.Tasks.Task HandleAsync(global::{{model}} message,
                        global::System.Threading.CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new global::System.NotImplementedException("Implement event handling before registering this listener.");
                    }
                }
                """,
            _ => throw new ArgumentException("Unknown generator.")
        };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".cs");
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
        writer.Write(body);
        return path;
    }

    private static string EnsureSafeDirectory(string root, string relative)
    {
        var path = Path.GetFullPath(root);
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or "." or "..") throw new ArgumentException("Invalid generator output directory.");
            path = Path.Combine(path, part);
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to write through a linked output directory.");
        }
        return path;
    }

    private static async Task<int> SeedAsync(string project, string? seeder, int seconds, CancellationToken token)
    {
        if (seconds is < 1 or > 86400) throw new ArgumentException("--timeout must be between 1 and 86400 seconds.");
        if (seeder is not null && !Regex.IsMatch(seeder, "^[A-Za-z_][A-Za-z0-9_.]*\\z")) throw new ArgumentException("Invalid seeder type name.");
        var directory = Directory.CreateTempSubdirectory("caravel-seed-");
        var receipt = Path.Combine(directory.FullName, "result.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            string[] arguments = ["run", "--project", project, "--no-launch-profile", "--", "--caravel-db-seed", receipt, .. (seeder is null ? Array.Empty<string>() : ["--seeder", seeder])];
            var result = await Program.RunAsync("dotnet", arguments, false, timeout.Token, Path.GetDirectoryName(project));
            if (result.ExitCode != 0) return result.ExitCode;
            if (!File.Exists(receipt)) throw new InvalidOperationException("No seed completion receipt. Add `if (await app.RunCaravelSeedersAsync(args)) return;` before starting the application's host.");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, token));
            if (!document.RootElement.GetProperty("completed").GetBoolean()) throw new InvalidOperationException("Seed execution did not report completion.");
            Console.WriteLine("Database seeders completed.");
            return 0;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Database seeding exceeded {seconds} seconds and was canceled. Check the application hook and database state; partial writes depend on the seeder's transaction handling.");
        }
        finally { directory.Delete(recursive: true); }
    }
}
