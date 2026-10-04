using Caravel.Bosun;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Caravel.Bosun.Tests;

public sealed class BosunTests
{
    [Fact]
    public async Task SchemaDescribesNativeValueTypesRequiredInputsAndArity()
    {
        var root = Program.CreateCommand();
        // Metadata must follow symbols added to the command tree, without evaluating defaults.
        var optional = new Command("optional-test")
        {
            new Argument<int>("count") { DefaultValueFactory = _ => throw new InvalidOperationException("Do not evaluate defaults while describing commands.") }
        };
        root.Add(optional);
        using var output = new StringWriter();
        Assert.Equal(0, await root.Parse(["schema", "--json"]).InvokeAsync(new InvocationConfiguration { Output = output }));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var commands = document.RootElement.GetProperty("commands").EnumerateArray().ToDictionary(command => command.GetProperty("name").GetString()!);
        AssertSymbol("new", "arguments", "name", "System.String", true, 1, 1);
        AssertSymbol("make:listener", "options", "--event", "System.String", true, 1, 1);
        AssertSymbol("make:factory", "options", "--model", "System.String", true, 1, 1);
        AssertSymbol("make:listener", "options", "--namespace", "System.String", false, 1, 1);
        AssertSymbol("serve", "options", "--watch", "System.Boolean", false, 0, 1);
        AssertSymbol("migrate:fresh", "options", "--force", "System.Boolean", false, 0, 1);
        AssertSymbol("db:seed", "options", "--timeout", "System.Int32", false, 1, 1);
        AssertSymbol("optional-test", "arguments", "count", "System.Int32", false, 0, 1);
        Assert.Empty(commands["serve"].GetProperty("arguments").EnumerateArray());

        void AssertSymbol(string command, string collection, string name, string type, bool required, int min, int max)
        {
            var symbol = Assert.Single(commands[command].GetProperty(collection).EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == name);
            Assert.Equal(type, symbol.GetProperty("type").GetString());
            Assert.Equal(required, symbol.GetProperty("required").GetBoolean());
            Assert.Equal(min, symbol.GetProperty("arity").GetProperty("min").GetInt32());
            Assert.Equal(max, symbol.GetProperty("arity").GetProperty("max").GetInt32());
        }
    }

    [Fact]
    public void OnlyImplementedCommandsAreAdvertised()
    {
        var root = Program.CreateCommand();
        Assert.Equal(["new", "serve", "dev", "route:list", "doctor", "version", "schema", "make:model", "make:seeder", "make:factory", "make:job", "make:event", "make:listener", "make:migration", "migrate", "migrate:status", "migrate:rollback", "migrate:fresh", "db:seed"], root.Subcommands.Select(command => command.Name));
        Assert.Empty(root.Parse(["schema", "--json"]).Errors);
        Assert.NotEmpty(root.Parse(["db:wipe", "--force"]).Errors);
        Assert.NotEmpty(root.Parse(["new"]).Errors);
    }

    [Theory]
    [InlineData("10.0.401", true)]
    [InlineData("10.0.100\n", true)]
    [InlineData("10.0.100-preview.7", false)]
    [InlineData("10.0.100-rc.2", false)]
    [InlineData("11.0.100", false)]
    [InlineData("9.0.200", false)]
    [InlineData("10.0", false)]
    [InlineData(null, false)]
    public void DoctorRequiresStableNet10Sdk(string? version, bool supported) =>
        Assert.Equal(supported, Program.IsSupportedSdk(version));

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("bad name")]
    [InlineData("1App")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("LPT9")]
    public void RejectsUnsafeApplicationNames(string name)
    {
        Assert.Throws<ArgumentException>(() => Program.CreateApplication(Path.GetTempPath(), name, null));
    }

    [Fact]
    public void RazorStarterUsesFrameworkAndNeverOverwrites()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"caravel-bosun-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        try
        {
            var target = Program.CreateApplication(parent, "MyApp", null);
            Assert.Contains("Clinimatix.Caravel.AspNetCore", File.ReadAllText(Path.Combine(target, "MyApp.csproj")));
            Assert.Contains("builder.AddCaravel();", File.ReadAllText(Path.Combine(target, "Program.cs")));
            Assert.Contains("builder.Services.AddValidation();", File.ReadAllText(Path.Combine(target, "Program.cs")));
            Assert.Contains("ExportCaravelRoutesAsync(args)", File.ReadAllText(Path.Combine(target, "Program.cs")));
            Assert.Contains("@page", File.ReadAllText(Path.Combine(target, "Pages", "Index.cshtml")));
            Assert.False(File.Exists(Path.Combine(target, ".env")));
            File.WriteAllText(Path.Combine(target, "keep.txt"), "existing data");
            Assert.Throws<IOException>(() => Program.CreateApplication(parent, "MyApp", null));
            Assert.Equal("existing data", File.ReadAllText(Path.Combine(target, "keep.txt")));
            Assert.Single(Directory.GetDirectories(parent));
            Assert.Throws<ArgumentException>(() => Program.CreateApplication(parent, "InvalidSource", parent));
            Assert.False(Directory.Exists(Path.Combine(parent, "InvalidSource")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void ApiStarterOmitsRazorAndRejectsUnsupportedStacksBeforeWriting()
    {
        var parent = Directory.CreateTempSubdirectory("caravel-api-starter-");
        try
        {
            Assert.Throws<ArgumentException>(() => Program.CreateApplication(parent.FullName, "NotCreated", null, "unknown"));
            Assert.Empty(parent.EnumerateFileSystemInfos());
            var target = Program.CreateApplication(parent.FullName, "MyApi", null, "api");
            Assert.False(Directory.Exists(Path.Combine(target, "Pages")));
            Assert.False(File.Exists(Path.Combine(target, "package.json")));
            Assert.Contains("Microsoft.AspNetCore.OpenApi", File.ReadAllText(Path.Combine(target, "MyApi.csproj")));
            Assert.Contains("AddValidation()", File.ReadAllText(Path.Combine(target, "Program.cs")));
            Assert.Contains("IsDevelopment()", File.ReadAllText(Path.Combine(target, "Program.cs")));
            Assert.Throws<IOException>(() => Program.CreateApplication(parent.FullName, "MyApi", null, "api"));
            Assert.Single(parent.EnumerateDirectories());
        }
        finally { parent.Delete(recursive: true); }
    }

    [Fact]
    public void ProjectDiscoveryRejectsAmbiguityAndPreservesArguments()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"caravel projects {Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        try
        {
            Assert.Throws<ArgumentException>(() => Program.ResolveProject(parent));
            var project = Path.Combine(parent, "One.csproj");
            File.WriteAllText(project, "<Project />");
            Assert.Equal(project, Program.ResolveProject(parent));
            File.WriteAllText(Path.Combine(parent, "Two.csproj"), "<Project />");
            Assert.Throws<ArgumentException>(() => Program.ResolveProject(parent));
            var info = Program.StartInfo("dotnet", ["run", "--project", project, "--", "a;$(unsafe)"], true);
            Assert.False(info.UseShellExecute);
            Assert.Empty(info.Arguments);
            Assert.Equal(project, info.ArgumentList[2]);
            Assert.Equal("a;$(unsafe)", info.ArgumentList[4]);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void IdentityStarterBundlesEditableSourcesAndRefusesPartialFrameworkReferences()
    {
        var parent = Directory.CreateTempSubdirectory("caravel-identity-starter-");
        try
        {
            // A C# keyword is a valid directory/project name; the generated namespace must still compile.
            var target = Program.CreateApplication(parent.FullName, "class", null, "identity");
            Assert.Contains("namespace classApplication;", File.ReadAllText(Path.Combine(target, "Program.cs")));
            Assert.Contains("Clinimatix.Caravel.Auth", File.ReadAllText(Path.Combine(target, "class.csproj")));
            Assert.True(File.Exists(Path.Combine(target, "SessionEndpoints.cs")));
            Assert.True(File.Exists(Path.Combine(target, "WorkItemEndpoints.cs")));
            Assert.True(File.Exists(Path.Combine(target, "wwwroot", "work-items.js")));
            Assert.True(File.Exists(Path.Combine(target, ".config", "dotnet-tools.json")));
            Assert.True(File.Exists(Path.Combine(target, "Properties", "launchSettings.json")));
            Assert.True(File.Exists(Path.Combine(target, "Database", "Identity", "IdentityContextModelSnapshot.cs")));
            Assert.True(File.Exists(Path.Combine(target, "Database", "Queue", "QueueDbContextModelSnapshot.cs")));
            if (!OperatingSystem.IsWindows())
                Assert.DoesNotContain(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories), file => Path.GetFileName(file).Contains('\\'));
            Assert.True(File.Exists(Path.Combine(target, ".gitignore")));
            Assert.False(File.Exists(Path.Combine(target, "CounterEndpoints.cs")));
            Assert.DoesNotContain(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories), file => file.EndsWith(".db") || file.EndsWith(".env"));
            Assert.Throws<IOException>(() => Program.CreateApplication(parent.FullName, "class", null, "identity"));
            var incomplete = Path.Combine(parent.FullName, "incomplete");
            var web = Path.Combine(incomplete, "src", "Caravel.AspNetCore"); Directory.CreateDirectory(web);
            File.WriteAllText(Path.Combine(web, "Caravel.AspNetCore.csproj"), "<Project />");
            Assert.Throws<ArgumentException>(() => Program.CreateApplication(parent.FullName, "Missing", incomplete, "identity"));
            Assert.False(Directory.Exists(Path.Combine(parent.FullName, "Missing")));
            Assert.Empty(Directory.EnumerateDirectories(parent.FullName, ".caravel-*"));
        }
        finally { parent.Delete(recursive: true); }
    }

    [Fact]
    public async Task ProcessRunnerCapturesOutputAndPropagatesExitCode()
    {
        var success = await Program.RunAsync("dotnet", ["--version"], true, CancellationToken.None);
        Assert.True(success.ExitCode == 0, success.Stdout + success.Stderr);
        Assert.NotEmpty(success.Stdout);
        var failure = await Program.RunAsync("dotnet", ["not-a-real-caravel-command"], true, CancellationToken.None);
        Assert.NotEqual(0, failure.ExitCode);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Program.RunAsync("dotnet", ["--info"], true, canceled.Token));
    }
}
