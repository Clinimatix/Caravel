# Generate jobs, events and listeners

Bosun can generate the source files for queue jobs, events and event listeners:

```powershell
caravel make:job RebuildReport --project ./MyApp
caravel make:event ReportReady --project ./MyApp
caravel make:listener RecordReport --event App.Events.ReportReady --project ./MyApp
```

| Command | Creates | Namespace |
| --- | --- | --- |
| `make:job RebuildReport` | `App/Jobs/RebuildReport.cs`, with a `RebuildReport` payload record and a `RebuildReportHandler` | `App.Jobs` |
| `make:event ReportReady` | `App/Events/ReportReady.cs`, with the event record | `App.Events` |
| `make:listener RecordReport --event App.Events.ReportReady` | `App/Listeners/RecordReport.cs`, a listener for that event | `App.Listeners` |

Options and rules:

- `--namespace MyApp.Jobs` overrides the default namespace.
- `--event` takes the event's full type name, including its namespace.
- `--project` accepts a project file or a folder containing exactly one project, and defaults to the current folder.
- Names must start with an uppercase letter and use only letters, digits and underscores. Generic type names aren't supported.
- Existing files are never overwritten.

The generators only write source files. They don't build your app, install packages or edit `Program.cs`. Add the `Clinimatix.Caravel.Queues` or `Clinimatix.Caravel.Events` package to your app before compiling the generated code.

## Implement, then register

Generated handlers and listeners throw `NotImplementedException` until you fill them in, so an unfinished job can't quietly report success. Add your payload fields and logic, then register them:

```csharp
using App.Events;
using App.Jobs;
using App.Listeners;
using Caravel.Events;
using Caravel.Queues;

// Set up the queue database first; see the queues guide.
builder.Services.AddQueueJob<RebuildReport, RebuildReportHandler>("reports.rebuild.v1");
builder.Services.AddCaravelEvents().AddEventListener<ReportReady, RecordReport>();
```

A few tips:

- **Pick a stable, versioned job name** like `reports.rebuild.v1`. It's stored with each job, so changing it later strands jobs that are already queued.
- **Make job handlers safe to run twice.** Jobs are delivered at least once.
- **Get the tenant or account from `JobContext`**, not from fields in the job payload.
- **Events are in-process.** Dispatching one doesn't store it or retry it. Use a queue job when the work must survive a crash.

See [queues](QUEUES.md), [events](EVENTS.md) and [hosting](HOSTING.md) for the full setup.
