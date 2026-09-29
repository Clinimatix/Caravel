using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;

namespace Caravel.Bosun;

internal static class DevCommands
{
    internal static void AddTo(RootCommand root)
    {
        var project = new Option<string?>("--project") { Description = "Web application .csproj or directory (defaults to the current directory)." };
        var worker = new Option<string?>("--worker") { Description = "Optional worker .csproj or directory; the application owns its worker and scheduler registration." };
        var command = new Command("dev", "Watch a web application and optionally supervise one explicit worker; stop with Ctrl+C.") { project, worker };
        command.SetAction((result, token) => RunAsync(CreateStartInfos(result.GetValue(project), result.GetValue(worker)), token));
        root.Add(command);
    }

    internal static IReadOnlyList<ProcessStartInfo> CreateStartInfos(string? project, string? worker)
    {
        // Resolve every project before starting any application code.
        var webProject = Program.ResolveProject(project);
        var workerProject = worker is null ? null : Program.ResolveProject(worker);
        if (string.Equals(webProject, workerProject, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Select a separate worker project; the web project is already running under dotnet watch.");
        var processes = new List<ProcessStartInfo>
        {
            Start(webProject, ["watch", "--non-interactive", "--project", webProject, "run"])
        };
        if (workerProject is not null) processes.Add(Start(workerProject, ["run", "--project", workerProject]));
        return processes;

        static ProcessStartInfo Start(string path, string[] arguments)
        {
            var info = Program.StartInfo("dotnet", arguments, capture: false);
            info.WorkingDirectory = Path.GetDirectoryName(path)!;
            info.CreateNoWindow = true;
            return info;
        }
    }

    internal static async Task<int> RunAsync(IReadOnlyList<ProcessStartInfo> startInfos, CancellationToken token)
    {
        if (startInfos.Count is < 1 or > 2) throw new ArgumentException("A development session needs one web watcher and at most one worker.", nameof(startInfos));
        var processes = new List<Process>();
        var exitCode = 1;
        try
        {
            foreach (var info in startInfos)
            {
                token.ThrowIfCancellationRequested();
                var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {info.FileName}.");
                processes.Add(process);
                Console.WriteLine($"caravel dev: started {(processes.Count == 1 ? "web watcher" : "worker")} (PID {process.Id}).");
            }

            var exited = await Task.WhenAny(processes.Select(async process =>
            {
                await process.WaitForExitAsync();
                return process;
            })).WaitAsync(token);
            var stopped = await exited;
            Console.Error.WriteLine($"caravel dev: process {stopped.Id} exited with code {stopped.ExitCode}; stopping the development session.");
            // Every configured component is required for this long-running session.
            exitCode = stopped.ExitCode == 0 ? 1 : stopped.ExitCode;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            exitCode = 130;
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stopped = await Task.WhenAll(processes.Select(process => StopAsync(process, shutdown.Token)));
            if (stopped.Any(success => !success)) exitCode = 1;
            foreach (var process in processes) process.Dispose();
        }
        return exitCode;
    }

    private static async Task<bool> StopAsync(Process process, CancellationToken token)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(token);
            return true;
        }
        catch (InvalidOperationException) when (process.HasExited) { return true; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or AggregateException or OperationCanceledException)
        {
            Console.Error.WriteLine($"caravel dev: could not confirm shutdown of process {process.Id} within the shutdown window: {exception.Message}");
            return false;
        }
    }
}
