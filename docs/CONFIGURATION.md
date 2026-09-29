# Configuration

Caravel uses standard .NET configuration and adds support for a `.env` file. Anything you already do with `appsettings.json`, environment variables, or user secrets keeps working.

## Where settings come from

Later sources override earlier ones:

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. `.env`
4. Environment variables
5. Command-line arguments

User secrets and any sources you add yourself keep their usual .NET positions relative to these.

## Friendly keys

Use concise `.env` aliases for common settings. Each maps to a structured .NET key:

| Friendly key | Structured key |
| --- | --- |
| `APP_NAME` / `APP_ENV` | `Caravel:Name` / `Caravel:Environment` |
| `DB_CONNECTION` / `DB_DATABASE` | `Database:Driver` / `Database:Database` |
| `DB_HOST` / `DB_PORT` | `Database:Host` / `Database:Port` |
| `DB_USERNAME` / `DB_PASSWORD` | `Database:Username` / `Database:Password` |
| `CACHE_DRIVER` / `QUEUE_DRIVER` | `Cache:Driver` / `Queue:Driver` |
| `STORAGE_DRIVER` / `MAIL_DRIVER` | `Storage:Driver` / `Mail:Driver` |

If both forms appear in the same source, the structured key wins. The standard `__` separator (`Database__Host`) also works.

These keys only map settings. Caravel doesn't yet include cache, queue, storage, or mail drivers that read them.

## The `.env` file

```dotenv
# Comments and blank lines are fine
APP_NAME="My App"
export DB_HOST=localhost
DB_PASSWORD='single quotes work too'
GREETING="double quotes allow escapes like \n"
```

Supported: comments, blank lines, an optional `export` prefix, and single or double quotes (double quotes allow escape sequences).

Not supported: `${VARIABLE}` interpolation, multi-line values, and reloading while the app runs.

If a line can't be parsed, the error gives the line number without showing the value, so secrets don't end up in logs.

Keep `.env` out of source control. The repository's `.env.sample` shows example values only. To use a different file name, set `options.EnvironmentFile` in `AddCaravel`.

## Choosing the environment

Set `ASPNETCORE_ENVIRONMENT` (or `DOTNET_ENVIRONMENT`) as a real environment variable or launch-profile setting. .NET decides the environment before `.env` is read, so setting it in `.env` has no effect.

`APP_ENV` is just a label your app can read. It doesn't switch which `appsettings.{Environment}.json` file loads.
