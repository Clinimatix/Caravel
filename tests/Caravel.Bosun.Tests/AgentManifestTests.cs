using System.CommandLine;
using System.Text.Json;
using Caravel.Bosun;
using Xunit;

namespace Caravel.Bosun.Tests;

public sealed class AgentManifestTests
{
    [Fact]
    public async Task StaticManifestEmitsOnlyKnownPackageIdsAndNeverExecutesProject()
    {
        var directory = Directory.CreateTempSubdirectory("caravel-manifest-");
        try
        {
            var project = Path.Combine(directory.FullName, "secret-name.csproj");
            var source = """
                <Project Sdk="SecretSdk">
                  <PropertyGroup><Secret>TOP_SECRET</Secret><TargetFramework>secret-framework</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Clinimatix.Caravel.Queues" Version="TOP_SECRET" />
                    <PackageReference Include="clinimatix.caravel.queues" />
                    <PackageReference Include="Clinimatix.Caravel.Core" Condition="false" />
                    <PackageReference Include="Unknown.TOP_SECRET" />
                    <ProjectReference Include="TOP_SECRET.csproj" />
                  </ItemGroup>
                  <Target Name="Fail" BeforeTargets="Build"><Error Text="Must never execute" /></Target>
                </Project>
                """;
            File.WriteAllText(project, source);
            File.WriteAllText(Path.Combine(directory.FullName, ".env"), "TOP_SECRET");
            var (exit, output, error) = await Invoke(project);
            Assert.Equal(0, exit);
            Assert.Empty(error);
            Assert.DoesNotContain("TOP_SECRET", output);
            Assert.DoesNotContain("secret-", output);
            Assert.DoesNotContain(directory.FullName, output);
            Assert.DoesNotContain("SecretSdk", output);
            using var document = JsonDocument.Parse(output);
            var manifest = document.RootElement;
            Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("caravel.project-capabilities", manifest.GetProperty("kind").GetString());
            Assert.Equal(["Clinimatix.Caravel.Core", "Clinimatix.Caravel.Queues"], manifest.GetProperty("packages").EnumerateArray().Select(value => value.GetString()));
            var discovery = manifest.GetProperty("discovery").EnumerateArray().ToArray();
            Assert.Equal(["schema", "doctor", "route:list"], discovery.Select(item => item.GetProperty("command").GetString()));
            Assert.True(discovery.Single(item => item.GetProperty("command").GetString() == "route:list").GetProperty("executesApplication").GetBoolean());
            Assert.All(discovery, item => Assert.Contains(Program.CreateCommand().Subcommands, command => command.Name == item.GetProperty("command").GetString()));
            Assert.Equal(source, File.ReadAllText(project));
            Assert.Empty(directory.GetDirectories());
            Assert.Equal(output, (await Invoke(directory.FullName)).Output);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("<Project><TOP_SECRET></Project>")]
    [InlineData("<!DOCTYPE Project [<!ENTITY secret SYSTEM 'file:///TOP_SECRET'>]><Project>&secret;</Project>")]
    [InlineData("<TOP_SECRET />")]
    [InlineData(null)]
    public async Task ErrorsDoNotDiscloseProjectContentsOrPaths(string? xml)
    {
        var directory = Directory.CreateTempSubdirectory("caravel-manifest-");
        try
        {
            var project = Path.Combine(directory.FullName, "TOP_SECRET.csproj");
            File.WriteAllText(project, xml ?? new string('x', 1024 * 1024 + 1));
            var (exit, output, error) = await Invoke(project);
            Assert.Equal(1, exit);
            Assert.Empty(output);
            Assert.DoesNotContain("TOP_SECRET", error);
            Assert.DoesNotContain(directory.FullName, error);
            Assert.Contains("Cannot inspect project", error);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MissingOrAmbiguousProjectsFailWithoutPaths()
    {
        var directory = Directory.CreateTempSubdirectory("caravel-manifest-");
        try
        {
            foreach (var name in new[] { "one.csproj", "two.csproj" }) File.WriteAllText(Path.Combine(directory.FullName, name), "<Project />");
            Assert.Equal(1, (await Invoke(directory.FullName)).Exit);
            var result = await Invoke(Path.Combine(directory.FullName, "missing.csproj"));
            Assert.Equal(1, result.Exit);
            Assert.DoesNotContain(directory.FullName, result.Error);
        }
        finally { directory.Delete(true); }
    }

    private static async Task<(int Exit, string Output, string Error)> Invoke(string project)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await Program.CreateCommand().Parse(["agent:manifest", "--project", project, "--json"])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }
}
