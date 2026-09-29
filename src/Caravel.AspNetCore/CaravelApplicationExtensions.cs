using Caravel.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Caravel.AspNetCore;

public static class CaravelApplicationExtensions
{
    public static WebApplicationBuilder AddCaravel(this WebApplicationBuilder builder, Action<CaravelOptions>? configure = null)
    {
        var options = new CaravelOptions();
        configure?.Invoke(options);
        builder.Configuration.AddCaravelEnvironment(options.EnvironmentFile);
        builder.Services.AddCaravelProviders(options);
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        return builder;
    }

    /// <summary>Configures the native pipeline. Providers boot asynchronously at host startup before requests.</summary>
    public static WebApplication UseCaravel(this WebApplication app)
    {
        _ = app.Services.GetRequiredService<CaravelProviderLifecycle>();
        const string marker = "Caravel.AspNetCore.Configured";
        var properties = ((IApplicationBuilder)app).Properties;
        if (properties.ContainsKey(marker)) return app;
        properties[marker] = true;
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
                return Task.CompletedTask;
            });
            await next(context);
        });
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler();
            app.UseHsts();
        }
        app.UseStatusCodePages();
        app.UseRouting();
        if (app.Services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>() is not null)
            app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        return app;
    }

    /// <summary>Use when providers must finish booting before application routes are mapped.</summary>
    public static async ValueTask<WebApplication> UseCaravelAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        app.UseCaravel();
        await app.Services.GetRequiredService<CaravelProviderLifecycle>().StartAsync(cancellationToken);
        return app;
    }
}
