using System.Text;
using Caravel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using Provider = Caravel.Core.ServiceProvider;

namespace Caravel.Core.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void EnvironmentPrecedenceAndFriendlyKeysPreserveSourcePriority()
    {
        var directory = Directory.CreateTempSubdirectory("caravel-config-");
        var prefix = "CARAVEL_TEST_" + Guid.NewGuid().ToString("N") + "_";
        Environment.SetEnvironmentVariable(prefix + "APP_NAME", "process");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, ".env"), "APP_NAME=dotenv\nQUEUE_DRIVER=memory\nDB_PASSWORD='secret # literal'\nEMPTY=\n");
            var configuration = new ConfigurationBuilder().SetBasePath(directory.FullName)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Caravel:Name"] = "settings" })
                .AddEnvironmentVariables(prefix).AddCommandLine(["--APP_NAME=cli"])
                .AddCaravelEnvironment().Build();
            using var configurationLifetime = (IDisposable)configuration;
            var envProvider = Assert.Single(configuration.Providers.OfType<CaravelEnvironmentProvider>());
            Assert.True(envProvider.Source.FileProvider!.GetFileInfo(".env").Exists, $".env not visible at {directory.FullName}: {envProvider.Source.FileProvider.GetType().Name}");
            Assert.Equal("cli", configuration["Caravel:Name"]);
            Assert.Equal("memory", configuration["Queue:Driver"]);
            Assert.Equal("secret # literal", configuration["Database:Password"]);
            Assert.Equal("", configuration["EMPTY"]);
            var process = new ConfigurationBuilder().SetBasePath(directory.FullName)
                .AddEnvironmentVariables(prefix).AddCaravelEnvironment().Build();
            using var processLifetime = (IDisposable)process;
            Assert.Equal("process", process["Caravel:Name"]);
            Assert.Null(Environment.GetEnvironmentVariable(prefix + "QUEUE_DRIVER"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "APP_NAME", null);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DotEnvHandlesQuotesCommentsAndNeverLeaksInvalidSecrets()
    {
        var provider = new CaravelEnvironmentProvider(new CaravelEnvironmentSource());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("export APP_NAME=\"Hello\\nworld\" # comment\nHASH=a#b\nTRIM=hello # comment\nPATH='C:\\tools'"));
        provider.Load(stream);
        Assert.True(provider.TryGet("Caravel:Name", out var name));
        Assert.Equal("Hello\nworld", name);
        Assert.True(provider.TryGet("HASH", out var hash));
        Assert.Equal("a#b", hash);
        Assert.True(provider.TryGet("TRIM", out var trimmed));
        Assert.Equal("hello", trimmed);
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("DB_PASSWORD=\"top-secret"));
        var exception = Assert.Throws<FormatException>(() => provider.Load(invalid));
        Assert.Contains("line 1", exception.Message);
        Assert.DoesNotContain("top-secret", exception.Message);
    }

    [Theory]
    [InlineData(" =secret")]
    [InlineData("1KEY=secret")]
    [InlineData("KEY=\"secret\"trailing")]
    [InlineData("KEY WITH SPACES=secret")]
    public void MalformedDotEnvUsesSafeDiagnostic(string line)
    {
        var provider = new CaravelEnvironmentProvider(new CaravelEnvironmentSource());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line));
        Assert.Equal("Invalid .env syntax at line 1.", Assert.Throws<FormatException>(() => provider.Load(stream)).Message);
    }

    [Fact]
    public async Task DependenciesRegisterAndBootInOrderExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        services.AddCaravelProviders(new CaravelOptions().AddProvider<DependentProvider>().AddProvider<BaseProvider>());
        using var container = services.BuildServiceProvider();
        var lifecycle = container.GetRequiredService<CaravelProviderLifecycle>();
        await Task.WhenAll(lifecycle.StartAsync(default), lifecycle.StartAsync(default));
        Assert.Equal(new[] { "register-base", "register-dependent", "boot-base", "boot-dependent" }, container.GetRequiredService<List<string>>());
    }

    [Fact]
    public void MissingAndCircularDependenciesFailBeforeRegistration()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaravelProviders(new CaravelOptions().AddProvider<DependentProvider>()));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCaravelProviders(new CaravelOptions().AddProvider<CircularProvider>()));
        Assert.Throws<InvalidOperationException>(() => new CaravelOptions().AddProvider<BaseProvider>().AddProvider<BaseProvider>());
    }

    [Fact]
    public async Task BootFailurePropagatesAndIsNotSilentlyRetried()
    {
        var lifecycle = new CaravelProviderLifecycle([new FailingProvider()],
            new CaravelApplication(new ServiceCollection().BuildServiceProvider(), new ConfigurationBuilder().Build(), new TestEnvironment()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(default));
    }

    public sealed class BaseProvider : Provider
    {
        public override void Register(IServiceCollection services) => services.AddSingleton(new List<string> { "register-base" });
        public override async ValueTask BootAsync(CaravelApplication app, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);
            app.Services.GetRequiredService<List<string>>().Add("boot-base");
        }
    }

    public sealed class DependentProvider : Provider
    {
        public override IReadOnlyList<Type> Dependencies => [typeof(BaseProvider)];
        public override void Register(IServiceCollection services)
            => ((List<string>)services.Single(descriptor => descriptor.ServiceType == typeof(List<string>)).ImplementationInstance!).Add("register-dependent");
        public override ValueTask BootAsync(CaravelApplication app, CancellationToken cancellationToken)
        {
            app.Services.GetRequiredService<List<string>>().Add("boot-dependent");
            return ValueTask.CompletedTask;
        }
    }

    public sealed class CircularProvider : Provider
    {
        public override IReadOnlyList<Type> Dependencies => [typeof(CircularProvider)];
    }

    public sealed class FailingProvider : Provider
    {
        public override ValueTask BootAsync(CaravelApplication app, CancellationToken cancellationToken)
            => ValueTask.FromException(new InvalidOperationException("Boot failed."));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
