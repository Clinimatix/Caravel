using System.Reflection;
using System.Security;

namespace Caravel.Bosun;

internal static class IdentityStarter
{
    internal static void Write(string directory, string name, string? frameworkSource)
    {
        string[] packages = ["AspNetCore", "Clarion", "Auth", "Queues", "Mail"];
        var references = packages.Select(package =>
        {
            if (frameworkSource is null)
                return $"<PackageReference Include=\"Clinimatix.Caravel.{package}\" Version=\"{SecurityElement.Escape(Program.Version)}\" />";
            var project = Path.GetFullPath(Path.Combine(frameworkSource, "src", $"Caravel.{package}", $"Caravel.{package}.csproj"));
            if (!File.Exists(project)) throw new ArgumentException($"--framework-source is missing src/Caravel.{package}/Caravel.{package}.csproj.");
            return $"<ProjectReference Include=\"{SecurityElement.Escape(project)}\" />";
        }).ToArray();
        // Resources are bundled at build time; installed generation never reads a source checkout.
        var assembly = typeof(IdentityStarter).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.StartsWith("Identity/", StringComparison.Ordinal)))
        {
            var relative = resource["Identity/".Length..];
            var destination = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var content = reader.ReadToEnd().Replace("Caravel.IdentitySample", name + "Application", StringComparison.Ordinal)
                .Replace("{{APP_NAME}}", name, StringComparison.Ordinal);
            File.WriteAllText(destination, content);
        }
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
              </PropertyGroup>
              <ItemGroup>
                {{string.Join("\n    ", references)}}
                <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.12" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.12" />
                <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
                <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
    }
}
