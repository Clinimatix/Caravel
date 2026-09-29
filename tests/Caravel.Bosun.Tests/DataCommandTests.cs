using Caravel.Bosun;
using Xunit;

namespace Caravel.Bosun.Tests;

public sealed class DataCommandTests
{
    [Fact]
    public void DestructiveCommandsRefuseBeforeAnyProcessAndRollbackNeedsAnExplicitTarget()
    {
        Assert.Throws<ArgumentException>(() => DataCommands.PlanEf("migrate:fresh", "app.csproj", "app.csproj", null, null, false));
        Assert.Throws<ArgumentException>(() => DataCommands.PlanEf("migrate:rollback", "app.csproj", "app.csproj", null, "0", false));
        Assert.Throws<ArgumentException>(() => DataCommands.PlanEf("migrate:rollback", "app.csproj", "app.csproj", null, null, true));
        Assert.NotEmpty(Program.CreateCommand().Parse(["migrate:rollback", "--force"]).Errors);
        Assert.NotEmpty(Program.CreateCommand().Parse(["migrate", "InitialCreate"]).Errors);
        Assert.NotEmpty(Program.CreateCommand().Parse(["migrate", "--connection", "must-not-be-accepted"]).Errors);
        Assert.NotEmpty(Program.CreateCommand().Parse(["make:factory", "UserFactory"]).Errors);
    }

    [Fact]
    public void EfPlansUseLocalToolsExplicitProjectsAndSeparateArguments()
    {
        var project = Path.Combine(Path.GetTempPath(), "application with spaces", "App.csproj");
        var startup = Path.Combine(Path.GetTempPath(), "startup", "Web.csproj");
        var plan = Assert.Single(DataCommands.PlanEf("make:migration", project, startup, "App.Data.AppDbContext", "InitialCreate", false));
        Assert.Equal(["tool", "run", "dotnet-ef", "--", "migrations", "add", "InitialCreate", "--output-dir", "Database/Migrations", "--project", project, "--startup-project", startup, "--context", "App.Data.AppDbContext"], plan);
        var status = Assert.Single(DataCommands.PlanEf("migrate:status", project, startup, null, null, false, json: true, noConnect: true));
        Assert.Contains("--json", status);
        Assert.Contains("--no-connect", status);
        Assert.DoesNotContain("--connection", status);
        var fresh = DataCommands.PlanEf("migrate:fresh", project, startup, null, null, true);
        Assert.Equal(3, fresh.Count);
        Assert.Contains("--no-connect", fresh[0]);
        Assert.Contains("drop", fresh[1]);
        Assert.Contains("--force", fresh[1]);
        Assert.Contains("update", fresh[2]);
        Assert.Contains("--no-build", fresh[2]);
        var rollback = Assert.Single(DataCommands.PlanEf("migrate:rollback", project, startup, null, "0", true));
        Assert.Equal("0", rollback[6]);
        Assert.All(new[] { "../../escape", "--connection", "Name;bad" }, name =>
            Assert.Throws<ArgumentException>(() => DataCommands.PlanEf("make:migration", project, startup, null, name, false)));
    }

    [Fact]
    public void LocalManifestMustPinStableEf10AndHonorsRootBoundaries()
    {
        var root = Directory.CreateTempSubdirectory("caravel-data-command-test-");
        try
        {
            var config = Directory.CreateDirectory(Path.Combine(root.FullName, ".config"));
            var manifest = Path.Combine(config.FullName, "dotnet-tools.json");
            File.WriteAllText(manifest, """{"version":1,"isRoot":true,"tools":{}}""");
            Assert.Throws<InvalidOperationException>(() => DataCommands.RequireLocalEfTool(root.FullName));
            foreach (var version in new[] { "10.0.12-preview.1", "11.0.0", "10.*", "10.0.12\\n" })
            {
                File.WriteAllText(manifest, """{"version":1,"isRoot":true,"tools":{"dotnet-ef":{"version":"VERSION","commands":["dotnet-ef"]}}}""".Replace("VERSION", version));
                Assert.Throws<InvalidOperationException>(() => DataCommands.RequireLocalEfTool(root.FullName));
            }
            File.WriteAllText(manifest, """{"version":1,"isRoot":true,"tools":{"dotnet-ef":{"version":"10.0.12","commands":["dotnet-ef"]}}}""");
            var child = Directory.CreateDirectory(Path.Combine(root.FullName, "child"));
            Assert.Equal(manifest, DataCommands.RequireLocalEfTool(child.FullName));
            Directory.CreateDirectory(Path.Combine(child.FullName, ".config"));
            File.WriteAllText(Path.Combine(child.FullName, ".config", "dotnet-tools.json"), """{"version":1,"isRoot":true,"tools":{}}""");
            Assert.Throws<InvalidOperationException>(() => DataCommands.RequireLocalEfTool(child.FullName));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void GeneratorsRejectTraversalAndNeverOverwriteUserSource()
    {
        var root = Directory.CreateTempSubdirectory("caravel-data-command-test-");
        try
        {
            var project = Path.Combine(root.FullName, "Example.csproj");
            File.WriteAllText(project, "<Project />");
            Assert.Throws<ArgumentException>(() => DataCommands.Generate("model", project, "../Escape", "App.Models", "App/Models"));
            Assert.Throws<ArgumentException>(() => DataCommands.Generate("model", project, "User", "App.Models;bad", "App/Models"));
            Assert.Throws<ArgumentException>(() => DataCommands.Generate("model", project, "CON", "App.Models", "App/Models"));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "App")));
            var model = DataCommands.Generate("model", project, "User", "App.Models", "App/Models");
            Assert.Contains("[ClarionModel]", File.ReadAllText(model));
            File.WriteAllText(model, "existing source");
            Assert.Throws<IOException>(() => DataCommands.Generate("model", project, "User", "App.Models", "App/Models"));
            Assert.Equal("existing source", File.ReadAllText(model));
            var seeder = DataCommands.Generate("seeder", project, "UsersSeeder", "App.Database.Seeders", "Database/Seeders");
            Assert.Contains("IClarionSeeder", File.ReadAllText(seeder));
            Assert.Contains("NotImplementedException", File.ReadAllText(seeder));
            var factory = DataCommands.Generate("factory", project, "UserFactory", "App.Database.Factories", "Database/Factories", "App.Models.User");
            Assert.Contains("ModelFactory<App.Models.User>", File.ReadAllText(factory));
            Assert.Throws<ArgumentException>(() => DataCommands.Generate("factory", project, "BrokenFactory", "App.Database.Factories", "Database/Factories", "App.User;bad"));
            Assert.Throws<ArgumentException>(() => DataCommands.Generate("factory", project, "BrokenFactory", "App.Database.Factories", "Database/Factories", "App.User\n"));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task IdentifierValidationRejectsTrailingNewlinesBeforeWritingOrLaunching()
    {
        var root = Directory.CreateTempSubdirectory("caravel-strict-identifiers-");
        try
        {
            var project = Path.Combine(root.FullName, "App.csproj");
            File.WriteAllText(project, "<Project />");
            Assert.Throws<ArgumentException>(() => Program.CreateApplication(root.FullName, "NewApp\n", null));
            Assert.Throws<ArgumentException>(() => DataCommands.PlanEf("make:migration", project, project, null, "Initial\n", false));
            Assert.Throws<ArgumentException>(() => DataCommands.PlanEf("migrate:rollback", project, project, null, "0\n", true));
            Assert.Throws<ArgumentException>(() => DataCommands.PlanEf("migrate", project, project, "App.Context\n", null, false));
            Assert.Equal(1, await Program.Main(["db:seed", "--project", project, "--seeder", "App.Seeder\n"]));
            Assert.Single(root.EnumerateFileSystemInfos());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void ServiceCommandsRequireExplicitListenerTypeAndAdvertiseSourceOnlyEffects()
    {
        var root = Program.CreateCommand();
        Assert.Empty(root.Parse(["make:job", "RebuildReport"]).Errors);
        Assert.Empty(root.Parse(["make:event", "ReportReady"]).Errors);
        Assert.Empty(root.Parse(["make:listener", "RecordReport", "--event", "App.Events.ReportReady"]).Errors);
        Assert.NotEmpty(root.Parse(["make:listener", "RecordReport"]).Errors);
        foreach (var kind in new[] { "job", "event", "listener" })
            Assert.Contains("Does not execute application code", DataCommands.Effects("make:" + kind));
    }

    [Theory]
    [InlineData("job", "App/Jobs", "App.Jobs", "RebuildReport")]
    [InlineData("event", "App/Events", "App.Events", "ReportReady")]
    [InlineData("listener", "App/Listeners", "App.Listeners", "RecordReport")]
    public void ServiceGeneratorsGuardPathsAndPreserveExistingSource(string kind, string folder, string ns, string name)
    {
        var root = Directory.CreateTempSubdirectory("caravel-service-generator-");
        try
        {
            var project = Path.Combine(root.FullName, "Example.csproj");
            File.WriteAllText(project, "<Project />");
            foreach (var invalid in new[] { "../Escape", "..\\Escape", "CON", "Bad;Source", "ValidName\n" })
                Assert.Throws<ArgumentException>(() => DataCommands.Generate(kind, project, invalid, ns, folder, "App.Events.ReportReady"));
            Assert.Throws<ArgumentException>(() => DataCommands.Generate(kind, project, name, "App;Bad", folder, "App.Events.ReportReady"));
            Assert.Throws<ArgumentException>(() => DataCommands.Generate(kind, project, name, "App.Valid\n", folder, "App.Events.ReportReady"));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "App")));
            var path = DataCommands.Generate(kind, project, name, ns, folder, "App.Events.ReportReady");
            var source = File.ReadAllText(path);
            if (kind == "event") Assert.Contains("public sealed record ReportReady;", source);
            else
            {
                Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", source);
                Assert.Contains("NotImplementedException", source);
                Assert.Contains(kind == "job" ? "IJobHandler<RebuildReport>" : "IEventListener<global::App.Events.ReportReady>", source);
            }
            File.WriteAllText(path, "user source");
            Assert.Throws<IOException>(() => DataCommands.Generate(kind, project, name, ns, folder, "App.Events.ReportReady"));
            Assert.Equal("user source", File.ReadAllText(path));
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ReportReady")]
    [InlineData("App.Events.ReportReady;Bad")]
    [InlineData("App.Events.ReportReady\n")]
    [InlineData("../ReportReady")]
    public void ListenerRejectsInvalidEventTypesBeforeWriting(string? eventType)
    {
        var root = Directory.CreateTempSubdirectory("caravel-listener-type-");
        try
        {
            Assert.Throws<ArgumentException>(() => DataCommands.Generate("listener", Path.Combine(root.FullName, "App.csproj"),
                "RecordReport", "App.Listeners", "App/Listeners", eventType));
            Assert.Empty(root.EnumerateFileSystemInfos());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void ServiceGeneratorRefusesLinkedOutputDirectory()
    {
        var root = Directory.CreateTempSubdirectory("caravel-linked-generator-");
        try
        {
            var target = root.CreateSubdirectory("target");
            Directory.CreateSymbolicLink(Path.Combine(root.FullName, "App"), target.FullName);
            Assert.Throws<IOException>(() => DataCommands.Generate("job", Path.Combine(root.FullName, "App.csproj"),
                "RebuildReport", "App.Jobs", "App/Jobs"));
            Assert.Empty(target.EnumerateFileSystemInfos());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void DataCommandSchemaDoesNotClaimReadOnlyDatabaseMutations()
    {
        Assert.Contains("drops", DataCommands.Effects("migrate:fresh"));
        Assert.Contains("Down", DataCommands.Effects("migrate:rollback"));
        Assert.Contains("writes", DataCommands.Effects("db:seed"));
        Assert.Contains("reads database", DataCommands.Effects("migrate:status"));
    }
}
