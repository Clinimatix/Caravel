using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Caravel.AspNetCore;

public sealed class RouteSet(IEndpointRouteBuilder endpoints)
{
    public RouteHandlerBuilder Get(string pattern, Delegate handler) => endpoints.MapGet(pattern, handler);
    public RouteHandlerBuilder Post(string pattern, Delegate handler) => endpoints.MapPost(pattern, handler);
    public RouteHandlerBuilder Put(string pattern, Delegate handler) => endpoints.MapPut(pattern, handler);
    public RouteHandlerBuilder Patch(string pattern, Delegate handler) => endpoints.MapPatch(pattern, handler);
    public RouteHandlerBuilder Delete(string pattern, Delegate handler) => endpoints.MapDelete(pattern, handler);

    public RouteGroupBuilder Group(string prefix, Action<RouteSet> configure)
    {
        var group = endpoints.MapGroup(prefix);
        configure(new RouteSet(group));
        return group;
    }

    public RouteSet Authorize(params string[] policies)
    {
        if (endpoints is not RouteGroupBuilder group)
            throw new InvalidOperationException("Authorize a route or a group; root-wide authorization uses a fallback policy.");
        group.RequireAuthorization(policies);
        return this;
    }
}

public sealed record CaravelRoute(IReadOnlyList<string> Methods, string Path, string? Name, string Handler);

public static class CaravelRoutingExtensions
{
    public static IEndpointRouteBuilder Routes(this IEndpointRouteBuilder endpoints, Action<RouteSet> configure)
    {
        configure(new RouteSet(endpoints));
        return endpoints;
    }

    public static RouteHandlerBuilder Name(this RouteHandlerBuilder builder, string name) => builder.WithName(name);

    public static TBuilder Authorize<TBuilder>(this TBuilder builder, params string[] policies)
        where TBuilder : IEndpointConventionBuilder => builder.RequireAuthorization(policies);

    /// <summary>Reads native endpoint metadata, including endpoints mapped without the routing DSL.</summary>
    public static IReadOnlyList<CaravelRoute> GetCaravelRoutes(this IEndpointRouteBuilder endpoints)
        => endpoints.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(endpoint => new CaravelRoute(
                endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [],
                "/" + (endpoint.RoutePattern.RawText ?? "").TrimStart('/'),
                endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                DescribeHandler(endpoint)))
            .OrderBy(route => route.Path, StringComparer.Ordinal)
            .ThenBy(route => string.Join(',', route.Methods), StringComparer.Ordinal).ToArray();

    /// <summary>Explicit CLI inspection hook. Does not start the host or boot providers.</summary>
    public static async Task<bool> ExportCaravelRoutesAsync(this IEndpointRouteBuilder endpoints, string[] args,
        CancellationToken cancellationToken = default)
    {
        var index = Array.IndexOf(args, "--caravel-routes-json");
        if (index < 0) return false;
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("--caravel-routes-json requires an output file path.");
        // Never overwrite an existing file. Bosun reserves a random, private temporary directory.
        await using var output = new FileStream(args[index + 1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, endpoints.GetCaravelRoutes(), new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken);
        return true;
    }

    private static string DescribeHandler(RouteEndpoint endpoint)
    {
        var method = endpoint.Metadata.GetMetadata<MethodInfo>();
        return method is null ? endpoint.DisplayName ?? "<handler>" : $"{method.DeclaringType?.Name}.{method.Name}";
    }
}
