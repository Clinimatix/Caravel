using System.ComponentModel.DataAnnotations;
using Caravel.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddCaravel();
builder.Services.AddValidation();
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseCaravel();
app.UseHttpsRedirection();
app.Routes(routes =>
{
    routes.Get("/", () => "Hello from Clinimatix Caravel").Name("home");
    routes.Group("/api", api =>
    {
        api.Post("/ping", () => Results.Ok(new { message = "pong" })).Name("ping");
    });
});
// Native mapping exposes DTOs to the stable .NET validation source generator.
app.MapPost("/api/greetings", (GreetingRequest request) => new { message = $"Hello, {request.Name}" })
    .WithName("greetings.create");
if (app.Environment.IsDevelopment()) app.MapOpenApi();

// Inspection exits before listening or booting providers. Application top-level registration still runs.
if (await app.ExportCaravelRoutesAsync(args)) return;
await app.RunAsync();

public sealed record GreetingRequest([property: Required, StringLength(100, MinimumLength = 2)] string Name);
public partial class Program;
