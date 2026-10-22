using System.CommandLine;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Caravel.Bosun;

// Static source hints only. Never evaluate MSBuild, load application assemblies or read configuration.
internal static class AgentManifest
{
    private static readonly string[] Packages =
    [
        "Core", "AspNetCore", "Clarion", "Auth", "Auth.Windows", "Events", "Queues",
        "Scheduling", "Storage", "Storage.Azure", "Mail", "Notifications"
    ];

    internal static void AddTo(RootCommand root)
    {
        var project = new Option<string?>("--project") { Description = "One .csproj or directory; inspected as XML without evaluation." };
        var json = new Option<bool>("--json") { Description = "Output JSON (the only manifest format)." };
        var command = new Command("agent:manifest", "Read-only, model-free project capability hints; never builds or executes application code.") { project, json };
        command.SetAction(result =>
        {
            try
            {
                var manifest = Inspect(Program.ResolveProject(result.GetValue(project)));
                result.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(manifest, Program.JsonOptions));
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or XmlException or InvalidOperationException)
            {
                // Parser and filesystem exception messages can contain source contents or absolute paths.
                result.InvocationConfiguration.Error.WriteLine("Cannot inspect project. Specify one readable .csproj containing valid XML (maximum 1 MiB; DTDs prohibited).");
                return 1;
            }
        });
        root.Add(command);
    }

    internal static object Inspect(string project)
    {
        using var stream = File.OpenRead(project);
        if (stream.Length > 1024 * 1024) throw new InvalidOperationException("Project too large.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "Project") throw new XmlException("Expected project.");
        var references = document.Descendants().Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new
        {
            schemaVersion = 1,
            kind = "caravel.project-capabilities",
            product = "Clinimatix Caravel",
            toolVersion = Program.Version,
            inspection = "static-project-xml",
            packages = Packages.Select(name => "Clinimatix.Caravel." + name).Where(references.Contains).ToArray(),
            discovery = new[]
            {
                new { command = "schema", executesApplication = false, purpose = "Installed command/options contract; inspect effects before use." },
                new { command = "doctor", executesApplication = false, purpose = "Explicit SDK/development dependency diagnostics; starts diagnostic processes." },
                new { command = "route:list", executesApplication = true, purpose = "Explicit route inspection builds and runs application startup code; review first." }
            },
            limits = new[]
            {
                "Package presence is a source hint, not proof of configured or working capabilities.",
                "Conditions are not evaluated; imports, central versions, transitive dependencies and project references are not inspected.",
                "No configuration, environment, paths, versions from project XML, source code, database schema or application data is emitted.",
                "No application execution, model calls, MCP server, arbitrary execution tool or database access is provided."
            }
        };
    }
}
