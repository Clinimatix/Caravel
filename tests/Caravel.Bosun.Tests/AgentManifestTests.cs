using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Caravel.Bosun;
using Xunit;

namespace Caravel.Bosun.Tests;

public sealed class AgentManifestTests
{
    [Theory]
    [InlineData("utf8", 0)]
    [InlineData("utf8", 1)]
    [InlineData("utf8-bom", 0)]
    [InlineData("utf8-bom", 1)]
    [InlineData("utf16", 0)]
    [InlineData("utf16", 2)]
    public async Task ValidXmlFileLimitCountsEncodedBytesIncludingPreamble(string encodingName, int excessBytes)
    {
        var directory = Directory.CreateTempSubdirectory("caravel-manifest-size-");
        try
        {
            Encoding encoding = encodingName switch
            {
                "utf8" => new UTF8Encoding(false),
                "utf8-bom" => new UTF8Encoding(true),
                _ => new UnicodeEncoding(false, true)
            };
            const string prefix = "<Project><ItemGroup><PackageReference Include=\"Clinimatix.Caravel.Core\" /></ItemGroup><!--";
            const string suffix = "--></Project>";
            var expectedBytes = 1024 * 1024 + excessBytes;
            var paddingBytes = expectedBytes - encoding.GetPreamble().Length - encoding.GetByteCount(prefix + suffix);
            var xml = prefix + new string(' ', paddingBytes / encoding.GetByteCount(" ")) + suffix;
            var project = Path.Combine(directory.FullName, "SYNTHETIC_PRIVATE.csproj");
            File.WriteAllText(project, xml, encoding);
            var before = File.ReadAllBytes(project);
            Assert.Equal(expectedBytes, before.Length);
            // Prove that rejection cannot be explained by malformed XML or a wrong root.
            Assert.Equal("Project", XDocument.Load(project).Root!.Name.LocalName);

            var result = await Invoke(project);
            if (excessBytes == 0)
            {
                Assert.Equal(0, result.Exit);
                Assert.Empty(result.Error);
                using var manifest = JsonDocument.Parse(result.Output);
                Assert.Equal(["Clinimatix.Caravel.Core"], manifest.RootElement.GetProperty("packages")
                    .EnumerateArray().Select(value => value.GetString()));
                Assert.DoesNotContain(directory.FullName, result.Output);
                Assert.DoesNotContain("SYNTHETIC_PRIVATE", result.Output);
            }
            else
            {
                Assert.Equal(1, result.Exit);
                Assert.Empty(result.Output);
                Assert.Equal("Cannot inspect project. Specify one readable .csproj containing valid XML (maximum 1 MiB; DTDs prohibited).",
                    result.Error.TrimEnd());
            }
            Assert.Equal(before, File.ReadAllBytes(project));
            Assert.Single(directory.GetFiles());
            Assert.Empty(directory.GetDirectories());
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("<!DOCTYPE Project []><Project />")]
    [InlineData("<!DOCTYPE Project [<!ENTITY package 'Clinimatix.Caravel.Core'>]><Project><ItemGroup><PackageReference Include='&package;' /></ItemGroup></Project>")]
    [InlineData("<!DOCTYPE Project [<!ATTLIST PackageReference Include CDATA 'Clinimatix.Caravel.Core'>]><Project><ItemGroup><PackageReference /></ItemGroup></Project>")]
    public async Task ValidInternalDtdIsRejectedWithoutDisclosureOrMutation(string xml)
    {
        var directory = Directory.CreateTempSubdirectory("caravel-manifest-dtd-");
        try
        {
            var project = Path.Combine(directory.FullName, "SYNTHETIC_PRIVATE.csproj");
            File.WriteAllText(project, xml);
            var before = File.ReadAllBytes(project);
            // Parse only these bounded synthetic fixtures, never arbitrary project input.
            // A null resolver prevents external access; successful parsing isolates DTD policy.
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                MaxCharactersInDocument = 4096,
                MaxCharactersFromEntities = 1024
            }))
            {
                var parsed = XDocument.Load(reader);
                Assert.Equal("Project", parsed.Root!.Name.LocalName);
                Assert.NotNull(parsed.DocumentType);
            }
            var result = await Invoke(project);
            Assert.Equal(1, result.Exit);
            Assert.Empty(result.Output);
            Assert.Equal("Cannot inspect project. Specify one readable .csproj containing valid XML (maximum 1 MiB; DTDs prohibited).",
                result.Error.TrimEnd());
            Assert.Equal(before, File.ReadAllBytes(project));
            Assert.Single(directory.GetFiles());
            Assert.Empty(directory.GetDirectories());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task GuidanceMatchesCommandSchemaVersionAndExecutionEffects()
    {
        var directory = Directory.CreateTempSubdirectory("caravel-guidance-");
        try
        {
            var project = Path.Combine(directory.FullName, "app.csproj");
            File.WriteAllText(project, "<Project Sdk=\"MissingSdk\" />");
            var manifestResult = await Invoke(project);
            Assert.Equal(0, manifestResult.Exit);
            Assert.Empty(manifestResult.Error);
            using var manifest = JsonDocument.Parse(manifestResult.Output);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await Program.CreateCommand().Parse(["schema", "--json"])
                .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }));
            Assert.Empty(error.ToString());
            using var schema = JsonDocument.Parse(output.ToString());
            Assert.Equal(schema.RootElement.GetProperty("version").GetString(),
                manifest.RootElement.GetProperty("toolVersion").GetString());
            var commands = schema.RootElement.GetProperty("commands").EnumerateArray()
                .ToDictionary(command => command.GetProperty("name").GetString()!, StringComparer.Ordinal);
            foreach (var hint in manifest.RootElement.GetProperty("discovery").EnumerateArray())
            {
                Assert.True(commands.TryGetValue(hint.GetProperty("command").GetString()!, out var command));
                Assert.False(string.IsNullOrWhiteSpace(command.GetProperty("effects").GetString()));
            }
            Assert.Contains("diagnostic processes", commands["doctor"].GetProperty("effects").GetString());
            Assert.Contains("executes application startup code", commands["route:list"].GetProperty("effects").GetString());
            var routeOptions = commands["route:list"].GetProperty("options").EnumerateArray()
                .Select(option => option.GetProperty("name").GetString()).ToArray();
            Assert.Contains("--project", routeOptions);
            Assert.Contains("--json", routeOptions);
            Assert.Single(directory.GetFiles());
            Assert.Empty(directory.GetDirectories());
        }
        finally { directory.Delete(true); }
    }

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
    [InlineData("")]
    [InlineData("http://schemas.microsoft.com/developer/msbuild/2003")]
    public async Task OnlyDeclarativeMsBuildItemsSupplyPackageHints(string projectNamespace)
    {
        var directory = Directory.CreateTempSubdirectory("caravel-manifest-");
        try
        {
            var project = Path.Combine(directory.FullName, "app.csproj");
            File.WriteAllText(project, $$"""
                <Project xmlns="{{projectNamespace}}" xmlns:other="urn:foreign">
                  <ItemGroup><PackageReference Include="Clinimatix.Caravel.Core" /></ItemGroup>
                  <Choose><When Condition="false"><ItemGroup>
                    <PackageReference Include="Clinimatix.Caravel.Queues" />
                  </ItemGroup></When><Otherwise><ItemGroup>
                    <PackageReference Include="Clinimatix.Caravel.Mail" />
                  </ItemGroup></Otherwise></Choose>
                  <Target Name="Dynamic"><ItemGroup><PackageReference Include="Clinimatix.Caravel.Auth" /></ItemGroup></Target>
                  <other:ItemGroup><PackageReference Include="Clinimatix.Caravel.Events" /></other:ItemGroup>
                  <ItemGroup><other:PackageReference Include="Clinimatix.Caravel.Storage" /></ItemGroup>
                  <PropertyGroup><ItemGroup><PackageReference Include="Clinimatix.Caravel.Clarion" /></ItemGroup></PropertyGroup>
                  <PackageReference Include="Clinimatix.Caravel.Notifications" />
                </Project>
                """);
            var result = await Invoke(project);
            Assert.Equal(0, result.Exit);
            Assert.Empty(result.Error);
            using var document = JsonDocument.Parse(result.Output);
            Assert.Equal(["Clinimatix.Caravel.Core", "Clinimatix.Caravel.Queues", "Clinimatix.Caravel.Mail"],
                document.RootElement.GetProperty("packages").EnumerateArray().Select(value => value.GetString()));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("<Project xmlns='urn:TOP_SECRET' />")]
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
