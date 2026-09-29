# Development sessions

`caravel dev` runs your web app with file watching, and can run a background worker next to it, all from one terminal:

```powershell
caravel dev
caravel dev --project ./src/MyApi
caravel dev --project ./src/MyApi/MyApi.csproj --worker ./src/MyWorker/MyWorker.csproj
```

Each path can be a `.csproj` file or a folder containing exactly one project. The worker must be a separate project. Caravel checks both projects before starting either one.

## What it runs

- **The web app** runs under `dotnet watch`, so code changes are picked up as usual.
- **The worker**, if you add one, runs with `dotnet run`. Restart the session to pick up worker code changes.

Both processes start in their own project folders and use their normal launch settings, configuration and environment variables. Their output appears in the same terminal, and Bosun prints each process ID at startup. If your app uses HTTPS, set up the .NET development certificate first (`dotnet dev-certs https --trust`).

The worker is an ordinary .NET app. It registers its own queue worker, scheduler and database settings; `caravel dev` doesn't infer anything from your packages. Avoid registering the same background work in both the web app and the worker unless you really want two workers competing for jobs.

Starting a project builds and runs its code, which can restore packages and open network ports, so only run `caravel dev` on code you trust.

## Stopping the session

Press **Ctrl+C** to stop. Bosun stops both processes (and the processes they started), waits up to ten seconds for them to exit, and returns exit code 130.

If the web app or worker exits on its own, the whole session stops:

- A failing process's exit code is passed through.
- A process that exits successfully but unexpectedly returns 1, because the session is incomplete.
- A problem during shutdown also returns 1, with a message explaining what happened.

Stopping is abrupt: in-flight work can be interrupted, so use [durable jobs](QUEUES.md) for anything that must survive a restart. Processes that detach themselves from their parent may not be stopped, so don't use `caravel dev` to launch daemons or services.

One quirk of `dotnet watch`: if your web app exits, the watcher keeps running and waits for a file change. Bosun treats that as normal, not as a failure. See Microsoft's [dotnet watch documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch) for details.

## Not included yet

`caravel dev` currently covers the web app and one worker. Frontend build tools, Docker services, Aspire orchestration and automatic migrations aren't part of it yet. For a single app without supervision, use `caravel serve` or `caravel serve --watch`.
