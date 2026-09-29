using System.Diagnostics;
using Caravel.Bosun;
using Xunit;

namespace Caravel.Bosun.Tests;

public sealed class DevCommandTests(DevProcessFixture fixture) : IClassFixture<DevProcessFixture>
{
    [Fact]
    public void NativeArgumentsPreserveProjectPathsAndResolveBothBeforeStarting()
    {
        var web = Path.Combine(fixture.Directory, "Web app.csproj");
        var worker = Path.Combine(fixture.Directory, "Worker app.csproj");
        File.WriteAllText(web, "<Project />");
        File.WriteAllText(worker, "<Project />");
        var commands = DevCommands.CreateStartInfos(web, worker);
        Assert.Equal(["watch", "--non-interactive", "--project", web, "run"], commands[0].ArgumentList);
        Assert.Equal(["run", "--project", worker], commands[1].ArgumentList);
        Assert.All(commands, command =>
        {
            Assert.False(command.UseShellExecute);
            Assert.True(command.CreateNoWindow);
            Assert.False(command.RedirectStandardOutput);
            Assert.Equal(fixture.Directory, command.WorkingDirectory);
        });
        Assert.Single(DevCommands.CreateStartInfos(web, null));
        Assert.Throws<ArgumentException>(() => DevCommands.CreateStartInfos(web, web));
        Assert.Throws<ArgumentException>(() => DevCommands.CreateStartInfos(web, Path.Combine(fixture.Directory, "missing.csproj")));
        Assert.Empty(Program.CreateCommand().Parse(["dev", "--project", web, "--worker", worker]).Errors);
    }

    [Fact]
    public async Task CancellationStopsBothOwnedRootsAndTheirLiveDescendants()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var web = fixture.Child(tree: true);
        var worker = fixture.Child(tree: true);
        var run = DevCommands.RunAsync([web.StartInfo, worker.StartInfo], cancel.Token);
        try
        {
            var pids = (await web.WaitReadyAsync()).Concat(await worker.WaitReadyAsync()).ToArray();
            Assert.Equal(4, pids.Length);
            Assert.All(pids, pid => Assert.True(IsRunning(pid)));
            cancel.Cancel();
            Assert.Equal(130, await run.WaitAsync(TimeSpan.FromSeconds(15)));
            await AssertStoppedAsync(pids);
        }
        finally { cancel.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    [Theory]
    [InlineData(37, 37)]
    [InlineData(0, 1)]
    public async Task WorkerExitStopsWatcherTreeAndReturnsFailure(int childExit, int expected)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var web = fixture.Child(tree: true);
        var worker = fixture.Child(tree: false, childExit);
        var run = DevCommands.RunAsync([web.StartInfo, worker.StartInfo], cancel.Token);
        try
        {
            var pids = (await web.WaitReadyAsync()).Concat(await worker.WaitReadyAsync()).ToArray();
            await File.WriteAllTextAsync(worker.ReleasePath, "exit");
            Assert.Equal(expected, await run.WaitAsync(TimeSpan.FromSeconds(15)));
            await AssertStoppedAsync(pids);
        }
        finally { cancel.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(15)); }
    }

    [Fact]
    public async Task FailedSecondStartStillStopsFirstProcess()
    {
        var web = fixture.Child(tree: false);
        var missing = new ProcessStartInfo(Path.Combine(fixture.Directory, "missing-executable")) { UseShellExecute = false };
        // The first process may be killed before its managed entry point writes a receipt.
        // Its bounded return plus a separate cancellation test cover both startup paths.
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            DevCommands.RunAsync([web.StartInfo, missing], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));
        if (File.Exists(web.ReadyPath)) await AssertStoppedAsync(await web.WaitReadyAsync());
    }

    [Fact]
    public async Task PreCanceledSessionStartsNothing()
    {
        var child = fixture.Child(tree: false);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Equal(130, await DevCommands.RunAsync([child.StartInfo], cancel.Token));
        Assert.False(File.Exists(child.ReadyPath));
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task AssertStoppedAsync(int[] pids)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (pids.Any(IsRunning)) await Task.Delay(25, timeout.Token);
        Assert.All(pids, pid => Assert.False(IsRunning(pid)));
    }
}

public sealed class DevProcessFixture : IAsyncLifetime
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("caravel dev processes ").FullName;
    private string Dll => Path.Combine(Directory, "bin", "Debug", "net10.0", "Child.dll");

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(Directory, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
        await File.WriteAllTextAsync(Path.Combine(Directory, "Child.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(Directory, "Program.cs"), """
            using System.Diagnostics;
            using System.Reflection;
            var ready = args[0];
            var release = args[1];
            var tree = bool.Parse(args[2]);
            var pids = new List<int> { Environment.ProcessId };
            if (tree)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                info.ArgumentList.Add(ready + ".child");
                info.ArgumentList.Add(release + ".child");
                info.ArgumentList.Add("false");
                info.ArgumentList.Add("0");
                using var child = Process.Start(info)!;
                pids.Add(child.Id);
                while (!File.Exists(ready + ".child")) await Task.Delay(10);
            }
            await File.WriteAllTextAsync(ready + ".tmp", string.Join(',', pids));
            File.Move(ready + ".tmp", ready);
            while (!File.Exists(release)) await Task.Delay(10);
            return int.Parse(args[3]);
            """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await Program.RunAsync("dotnet", ["build", Path.Combine(Directory, "Child.csproj"), "--nologo"], true, timeout.Token);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
    }

    public ChildProcess Child(bool tree, int exitCode = 0)
    {
        var ready = Path.Combine(Directory, Guid.NewGuid().ToString("N"));
        var info = Program.StartInfo("dotnet", [Dll, ready, ready + ".release", tree.ToString(), exitCode.ToString()], false);
        info.CreateNoWindow = true;
        return new ChildProcess(info, ready, ready + ".release");
    }

    public Task DisposeAsync()
    {
        System.IO.Directory.Delete(Directory, recursive: true);
        return Task.CompletedTask;
    }

    public sealed record ChildProcess(ProcessStartInfo StartInfo, string ReadyPath, string ReleasePath)
    {
        public async Task<int[]> WaitReadyAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ReadyPath)) await Task.Delay(10, timeout.Token);
            return (await File.ReadAllTextAsync(ReadyPath, timeout.Token)).Split(',').Select(int.Parse).ToArray();
        }
    }
}
