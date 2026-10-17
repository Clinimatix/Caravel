using System.Reflection;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Caravel.App.Tests;

public class RuntimeSecurityTests
{
    [Fact]
    public void QualificationUsesServicedDotNetAndAspNetCoreRuntimes()
    {
        // Inspect the loaded runtime, not the NuGet graph or SDK inventory.
        var minimum = new Version(10, 0, 13);
        Assert.True(Environment.Version >= minimum,
            $"Qualification requires .NET {minimum} or later; loaded {Environment.Version}.");
        var informational = typeof(HttpContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        var aspNetVersion = Version.Parse(informational.Split('+')[0]);
        Assert.True(aspNetVersion >= minimum,
            $"Qualification requires ASP.NET Core {minimum} or later; loaded {informational}.");
    }
}
