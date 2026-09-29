# Hosting web apps and workers

Caravel apps are ordinary .NET apps. Web apps run on ASP.NET Core and background workers run on the .NET Generic Host, so you deploy them the same way you'd deploy any other .NET app: as a Windows Service, an IIS site, a Linux service or a container.

For local development, use [development sessions](DEVELOPMENT.md). For a complete, runnable worker, see the [queue sample](QUEUES.md#try-the-runnable-worker-sample).

## Run a durable worker

After registering your database queue and job handlers, add the hosted worker:

```csharp
builder.Services.AddCaravelQueueWorker("reports");
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});
await builder.Build().RunAsync();
```

Each worker processes one job at a time. How it behaves when things go wrong:

- **Shutdown** cancels the running handler's `CancellationToken`. Pass that token to your own async calls so work stops promptly. An interrupted job is picked up again after its lease expires.
- **Handler exceptions** go through the queue's retry and dead-letter handling.
- **Infrastructure failures**, such as the database being unreachable, stop the host when `BackgroundServiceExceptionBehavior` is `StopHost` (the .NET default). Don't switch to `Ignore` just to keep the process alive: the worker would stay stopped while the process looks healthy.

Keep handlers safe to run twice, and pick a lease and shutdown timeout that suit your jobs.

A stopped host doesn't automatically mean a nonzero exit code or a restart. The [worker sample](QUEUES.md#try-the-runnable-worker-sample) checks whether its worker faulted and exits with code 1 if so. Configure your service manager or orchestrator to restart the process on failure. Microsoft's [Windows worker guide](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service) covers this for Windows Services.

## Run as a Windows Service

Add the `Microsoft.Extensions.Hosting.WindowsServices` package to your app and register it before building the host:

```csharp
builder.Services.AddWindowsService(options => options.ServiceName = "My application worker");
```

Then publish the app and install it as a service with your usual deployment tooling. Caravel doesn't install services for you. Microsoft's [worker service guide](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service) walks through creating the service, setting its account and configuring recovery.

Two things catch people out:

- **The working directory** of a Windows Service is usually `C:\Windows\System32`. Configure absolute paths for databases, storage and configuration files.
- **HTTPS** needs a real certificate. The ASP.NET Core development certificate doesn't work for services. See [hosting ASP.NET Core in a Windows Service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0).

## IIS, Linux services and containers

Use the standard .NET hosting integration for your platform. Your job handlers don't need to change.

You can run the worker inside the web process, but then the web server's recycling and scaling also control your background work. A separate worker process keeps the two lifecycles independent. Don't use `dotnet watch` or `caravel dev` as a production process manager.

If you run development tools or tests that start child processes inside a Linux container, start the container with Docker's `--init` option. Without an init process, finished child processes can linger as zombies. See Docker's [guidance on running multiple processes](https://docs.docker.com/engine/containers/multi-service_container/).

## Keep users signed in across deployments

Identity cookies and account tokens are protected by ASP.NET Core Data Protection. For production, configure a persistent key store so sign-ins survive restarts and deployments:

- Give the app a stable application name, so every instance of the same app can read the same keys, and different apps stay isolated.
- Protect the keys at rest. A plain filesystem key store isn't encrypted unless you configure it.
- Keep older keys available during rotation.

Microsoft's [Data Protection configuration guide](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0) covers the options. To sign out a particular user everywhere, use the [security stamp](AUTHENTICATION.md#change-a-password-or-revoke-sessions), not key rotation. Losing or changing the key store signs out everyone.

## Before you go live

- Apply reviewed migrations before starting web apps and workers. Starting a host doesn't create the queue tables.
- Try a restart with pending and in-progress jobs, a failing handler, a database outage and an upgrade, and confirm your service manager restarts a stopped worker.
- Watch [queue metrics](OBSERVABILITY.md) as well as process health. A web app answering health checks doesn't mean jobs are being processed.
