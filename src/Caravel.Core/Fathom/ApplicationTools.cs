using System.Collections.ObjectModel;
using System.Security.Claims;
using System.Text.Json;

namespace Caravel.Fathom;

/// <summary>Application-declared effects. Metadata does not enforce read-only behavior.</summary>
public enum ToolEffect { Read, Write }

/// <summary>Static metadata suitable for inspection without constructing a handler.</summary>
public sealed record ToolDescriptor
{
    public string Name { get; }
    public ToolEffect Effect { get; }

    public ToolDescriptor(string name, ToolEffect effect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Tool names must contain 1–64 ASCII letters, digits, underscores or hyphens.", nameof(name));
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        Name = name;
        Effect = effect;
    }
}

/// <summary>Checks current application permissions for every call, including reads.</summary>
public interface IApplicationToolAuthorizer
{
    ValueTask<bool> AuthorizeAsync(ToolDescriptor tool, ClaimsPrincipal principal,
        JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>A deliberately registered application operation; never discovered through reflection.</summary>
public sealed class ApplicationTool
{
    public ToolDescriptor Descriptor { get; }
    internal Func<ClaimsPrincipal, JsonElement, CancellationToken, ValueTask<JsonElement>> Handler { get; }

    public ApplicationTool(ToolDescriptor descriptor,
        Func<ClaimsPrincipal, JsonElement, CancellationToken, ValueTask<JsonElement>> handler)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }
}

/// <summary>Opt-in, in-process dispatch. Owns no endpoints, services, providers or handler lifetimes.</summary>
public sealed class ApplicationToolRegistry
{
    private readonly Dictionary<string, ApplicationTool> tools = new(StringComparer.Ordinal);
    private readonly IApplicationToolAuthorizer authorizer;
    public ReadOnlyCollection<ToolDescriptor> Descriptors { get; }

    public ApplicationToolRegistry(IEnumerable<ApplicationTool> registrations, IApplicationToolAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        foreach (var tool in registrations)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (!tools.TryAdd(tool.Descriptor.Name, tool))
                throw new ArgumentException("Duplicate application tool name.", nameof(registrations));
        }
        Descriptors = Array.AsReadOnly(tools.Values.Select(t => t.Descriptor).ToArray());
    }

    public async ValueTask<JsonElement> ExecuteAsync(string name, ClaimsPrincipal principal,
        JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(principal);
        cancellationToken.ThrowIfCancellationRequested();
        if (!tools.TryGetValue(name, out var tool))
            throw new KeyNotFoundException("Application tool is not registered.");
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool arguments must be a JSON object.", nameof(arguments));
        // Take a stable snapshot before crossing the asynchronous authorization boundary.
        var snapshot = arguments.Clone();
        if (!await authorizer.AuthorizeAsync(tool.Descriptor, principal, snapshot, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Application tool execution denied.");
        cancellationToken.ThrowIfCancellationRequested();
        return await tool.Handler(principal, snapshot, cancellationToken).ConfigureAwait(false);
    }
}
