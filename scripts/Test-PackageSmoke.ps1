$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$run = Join-Path $root ('artifacts/smoke ' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $run 'packages'
$toolDirectory = Join-Path $run 'tool'
$null = New-Item -ItemType Directory -Path $packages -Force
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_NOLOGO = 'true'
$previousPackages = $env:NUGET_PACKAGES

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    Write-Host "Running: $Executable $($Arguments -join ' ')"
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    foreach ($name in @('Core', 'AspNetCore', 'Bosun', 'Clarion', 'Auth', 'Auth.Windows', 'Events', 'Queues', 'Storage', 'Scheduling')) {
        Invoke-Checked dotnet @('pack', "src/Caravel.$name/Caravel.$name.csproj", '-c', 'Release', '--no-restore', '-o', $packages)
    }
    # Never reuse an earlier package with the same pre-alpha version from a global cache.
    $env:NUGET_PACKAGES = Join-Path $run 'package-cache'
    $toolPackages = @(Get-ChildItem -LiteralPath $packages -Filter 'Clinimatix.Caravel.Bosun.*.nupkg')
    if ($toolPackages.Count -ne 1) { throw 'Expected exactly one Bosun package.' }
    $version = $toolPackages[0].BaseName.Substring('Clinimatix.Caravel.Bosun.'.Length)
    $feed = Join-Path $run 'NuGet.Config'
    $escaped = [System.Security.SecurityElement]::Escape($packages)
    "<configuration><packageSources><clear/><add key=`"local`" value=`"$escaped`"/></packageSources></configuration>" | Set-Content -LiteralPath $feed
    Invoke-Checked dotnet @('tool', 'install', 'Clinimatix.Caravel.Bosun', '--tool-path', $toolDirectory, '--version', $version, '--configfile', $feed)
    $tool = Join-Path $toolDirectory $(if ($IsWindows) { 'caravel.exe' } else { 'caravel' })
    Invoke-Checked $tool @('version')
    $schema = (& $tool schema --json) | ConvertFrom-Json
    $expectedCommands = @('new', 'serve', 'dev', 'route:list', 'doctor', 'version', 'schema', 'make:model', 'make:seeder', 'make:factory', 'make:job', 'make:event', 'make:listener', 'make:migration', 'migrate', 'migrate:status', 'migrate:rollback', 'migrate:fresh', 'db:seed')
    if ($LASTEXITCODE -ne 0 -or (Compare-Object ($schema.commands.name | Sort-Object) ($expectedCommands | Sort-Object))) { throw 'Installed tool schema mismatch.' }
    $listenerEvent = ($schema.commands | Where-Object name -eq 'make:listener').options | Where-Object name -eq '--event'
    $watch = ($schema.commands | Where-Object name -eq 'serve').options | Where-Object name -eq '--watch'
    $appName = ($schema.commands | Where-Object name -eq 'new').arguments | Where-Object name -eq 'name'
    if (-not $listenerEvent.required -or $listenerEvent.type -ne 'System.String' -or $listenerEvent.arity.min -ne 1 -or $listenerEvent.arity.max -ne 1 -or
        $watch.required -or $watch.type -ne 'System.Boolean' -or $watch.arity.min -ne 0 -or
        -not $appName.required -or $appName.arity.min -ne 1 -or $appName.arity.max -ne 1) {
        throw 'Installed tool argument/option schema mismatch.'
    }

    # Exercise both prepublication project references and the actual NuGet package dependency graph.
    Push-Location $run
    try {
        Invoke-Checked $tool @('new', 'SourceApp', '--framework-source', $root)
        Invoke-Checked $tool @('new', 'PackageApp')
        Invoke-Checked $tool @('new', 'SourceApi', '--stack', 'api', '--framework-source', $root)
        Invoke-Checked $tool @('new', 'PackageApi', '--stack', 'api')
        "<configuration><packageSources><clear/><add key=`"local`" value=`"$escaped`"/><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources></configuration>" | Set-Content -LiteralPath $feed
        foreach ($name in @('SourceApp', 'PackageApp', 'SourceApi', 'PackageApi')) {
            $project = Join-Path $run "$name/$name.csproj"
            Invoke-Checked dotnet @('restore', $project, '--configfile', $feed)
            Invoke-Checked dotnet @('build', $project, '--no-restore')
            $routes = (& $tool route:list --project $project --json) | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or -not ($routes | Where-Object { $_.name -eq 'hello' -and $_.path -eq '/hello' })) {
                throw "$name named endpoint was not exported."
            }
        }

        # Compile and execute generated service types against the installed package graph.
        $serviceDirectory = Join-Path $run 'ServiceApp'
        $null = New-Item -ItemType Directory -Path $serviceDirectory
        $serviceProject = Join-Path $serviceDirectory 'ServiceApp.csproj'
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Clinimatix.Caravel.Queues" Version="$version" />
    <PackageReference Include="Clinimatix.Caravel.Events" Version="$version" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $serviceProject
        Invoke-Checked $tool @('make:job', 'RebuildReport', '--project', $serviceProject)
        Invoke-Checked $tool @('make:event', 'ReportReady', '--project', $serviceProject)
        Invoke-Checked $tool @('make:listener', 'RecordReport', '--event', 'App.Events.ReportReady', '--project', $serviceProject)
        @'
using App.Events;
using App.Jobs;
using App.Listeners;
using Caravel.Events;
using Caravel.Queues;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddQueueJob<RebuildReport, RebuildReportHandler>("reports.rebuild.v1");
services.AddEventListener<ReportReady, RecordReport>();
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var handler = scope.ServiceProvider.GetRequiredService<RebuildReportHandler>();
var dispatcher = scope.ServiceProvider.GetRequiredService<IEventDispatcher>();
var context = new JobContext(Guid.NewGuid(), "reports", "synthetic", 1);
await MustThrow<NotImplementedException>(() => handler.HandleAsync(new RebuildReport(), context, default));
await MustThrow<NotImplementedException>(() => dispatcher.DispatchAsync(new ReportReady()));
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
await MustThrow<OperationCanceledException>(() => handler.HandleAsync(new RebuildReport(), context, cancelled.Token));
await MustThrow<OperationCanceledException>(() => new RecordReport().HandleAsync(new ReportReady(), cancelled.Token));
Console.WriteLine("Generated jobs and listeners compile, register, reject unfinished work and honor cancellation.");

static async Task MustThrow<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name} from the generated handler.");
}
'@ | Set-Content -LiteralPath (Join-Path $serviceDirectory 'Program.cs')
        Invoke-Checked dotnet @('restore', $serviceProject, '--configfile', $feed)
        Invoke-Checked dotnet @('run', '--project', $serviceProject, '--no-restore')

        # A disposable worker exits only after the HTTP checks release it.
        $workerDirectory = Join-Path $run 'DevWorker'
        $null = New-Item -ItemType Directory -Path $workerDirectory
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>' | Set-Content (Join-Path $workerDirectory 'DevWorker.csproj')
        @'
var deadline = DateTime.UtcNow.AddMinutes(2);
while (!File.Exists("stop-worker"))
{
    if (DateTime.UtcNow >= deadline) return 72;
    await Task.Delay(50);
}
return 37;
'@ | Set-Content (Join-Path $workerDirectory 'Program.cs')

        foreach ($scenario in @(
            @{ Name = 'PackageApp'; Environment = 'Development'; Command = 'serve' },
            @{ Name = 'PackageApi'; Environment = 'Development'; Command = 'serve' },
            @{ Name = 'PackageApi'; Environment = 'Production'; Command = 'serve' },
            @{ Name = 'PackageApi'; Environment = 'Development'; Command = 'dev' })) {
            # Use an ephemeral localhost HTTP profile only for this synthetic test; no certificate trust changes.
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            $profile = Join-Path $run "$($scenario.Name)/Properties/launchSettings.json"
            $settings = Get-Content -LiteralPath $profile -Raw | ConvertFrom-Json
            $settings.profiles.https.applicationUrl = "http://127.0.0.1:$port"
            $settings.profiles.https.environmentVariables.ASPNETCORE_ENVIRONMENT = $scenario.Environment
            $settings | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $profile
            $start = [Diagnostics.ProcessStartInfo]::new($tool)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.ArgumentList.Add($scenario.Command)
            $start.ArgumentList.Add('--project')
            $start.ArgumentList.Add((Join-Path $run "$($scenario.Name)/$($scenario.Name).csproj"))
            if ($scenario.Command -eq 'dev') {
                $start.ArgumentList.Add('--worker')
                $start.ArgumentList.Add((Join-Path $workerDirectory 'DevWorker.csproj'))
                $start.Environment['DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER'] = 'true'
                $start.Environment['DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH'] = 'true'
            }
            Write-Host "Starting $($scenario.Name) in $($scenario.Environment)."
            $process = [Diagnostics.Process]::Start($start)
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            try {
                $ready = $false
                for ($attempt = 0; $attempt -lt 60; $attempt++) {
                    if ($process.HasExited) { throw "Serve exited: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())" }
                    try {
                        $response = Invoke-WebRequest "http://127.0.0.1:$port/hello" -TimeoutSec 1
                        if ($response.Content -eq 'Hello from Clinimatix Caravel') { $ready = $true; break }
                    } catch { Start-Sleep -Milliseconds 500 }
                }
                if (-not $ready) { throw 'Generated application did not become ready.' }
                if ($scenario.Name -eq 'PackageApp') {
                    $page = Invoke-WebRequest "http://127.0.0.1:$port/" -TimeoutSec 5
                    if ($page.Content -notlike '*Built with the Clinimatix Caravel framework*') { throw 'Razor page did not render.' }
                } else {
                    $greeting = Invoke-RestMethod "http://127.0.0.1:$port/api/greetings" -Method Post -ContentType 'application/json' -Body '{"name":"Ada"}' -TimeoutSec 5
                    if ($greeting.message -ne 'Hello, Ada') { throw 'Generated API did not bind its request.' }
                    $invalid = Invoke-WebRequest "http://127.0.0.1:$port/api/greetings" -Method Post -ContentType 'application/json' -Body '{"name":"X"}' -SkipHttpErrorCheck -TimeoutSec 5
                    if ($invalid.StatusCode -ne 400 -or $invalid.Headers.'Content-Type' -notlike 'application/problem+json*') { throw 'Generated API did not validate its request.' }
                    if ((Invoke-WebRequest "http://127.0.0.1:$port/health/live" -TimeoutSec 5).Content -ne 'Healthy') { throw 'Liveness probe failed.' }
                    $openapi = Invoke-WebRequest "http://127.0.0.1:$port/openapi/v1.json" -SkipHttpErrorCheck -TimeoutSec 5
                    if ($scenario.Environment -eq 'Development') {
                        if ($openapi.StatusCode -ne 200 -or -not ($openapi.Content | ConvertFrom-Json).paths.'/api/greetings'.post) { throw 'Development OpenAPI document is missing.' }
                    } elseif ($openapi.StatusCode -ne 404) { throw 'OpenAPI must not be exposed by default in Production.' }
                }
                if ($scenario.Command -eq 'dev') {
                    'Release the synthetic worker.' | Set-Content (Join-Path $workerDirectory 'stop-worker')
                    if (-not $process.WaitForExit(20000)) { throw 'Development supervisor did not stop after its worker exited.' }
                    if ($process.ExitCode -ne 37) { throw "Development supervisor lost worker exit status: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())" }
                    $probe = [Net.Sockets.TcpClient]::new()
                    try {
                        try { $probe.Connect('127.0.0.1', $port) } catch [Net.Sockets.SocketException] { }
                        if ($probe.Connected) { throw 'Development web listener survived worker exit.' }
                    } finally { $probe.Dispose() }
                }
            } finally {
                Write-Host 'Stopping installed CLI and generated application.'
                if (-not $process.HasExited) { $process.Kill($true) }
                $process.WaitForExit()
                $process.Dispose()
            }
        }
        Write-Output "Package smoke passed: installed CLI, service generators, Razor/API starters, routes, validation, liveness, Development-only OpenAPI and supervised watcher/worker cleanup. Artifacts: $run"
    } finally { Pop-Location }
} finally { $env:NUGET_PACKAGES = $previousPackages; Pop-Location }
