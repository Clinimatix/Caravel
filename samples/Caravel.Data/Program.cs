using System.Text.Json;
using Caravel.Clarion;
using Caravel.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var database = Environment.GetEnvironmentVariable("CARAVEL_SAMPLE_DATABASE")
    ?? throw new InvalidOperationException("Set CARAVEL_SAMPLE_DATABASE to an explicit disposable SQLite file path.");
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddClarion<ActivityContext>(options => options.UseSqlite(
    new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(database) }.ToString()));
builder.Services.AddClarionSeeder<SyntheticActivitySeeder>();
using var app = builder.Build();
if (await app.RunCaravelSeedersAsync(args)) return;
await using var scope = app.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<IClarion>();
var summary = await db.Models<Activity>().AsNoTracking().GroupBy(x => x.Kind)
    .Select(group => new { kind = group.Key, events = group.Count(), quantity = group.Sum(x => x.Quantity) }).GetAsync();
Console.WriteLine(JsonSerializer.Serialize(summary));
