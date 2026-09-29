using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace Caravel.Core;

public static class CaravelConfigurationExtensions
{
    /// <summary>Inserts .env below environment/CLI sources. Never changes process environment variables.</summary>
    public static IConfigurationBuilder AddCaravelEnvironment(this IConfigurationBuilder builder, string path = ".env")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var sources = builder.Sources;
        if (sources.Any(source => source is CaravelEnvironmentSource))
            throw new InvalidOperationException("Clinimatix Caravel environment configuration is already registered.");
        // The default host places application environment and command-line providers after JSON/secrets.
        // WebApplicationBuilder also appends host/custom sources; they must retain their priority.
        var insertion = sources.ToList().FindLastIndex(source => source is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        if (insertion < 0) insertion = sources.ToList().FindLastIndex(source => source is EnvironmentVariablesConfigurationSource);
        if (insertion < 0) insertion = sources.ToList().FindLastIndex(source => source is CommandLineConfigurationSource);
        if (insertion < 0) insertion = sources.Count;
        sources.Insert(insertion, new CaravelEnvironmentSource { Path = path, Optional = true, ReloadOnChange = false });
        for (var index = 0; index < sources.Count; index++)
        {
            if (sources[index] is EnvironmentVariablesConfigurationSource environment)
                sources[index] = new FriendlyEnvironmentSource(environment.Prefix);
            else if (sources[index] is CommandLineConfigurationSource commandLine)
                sources[index] = new FriendlyCommandLineSource(commandLine);
        }
        return builder;
    }
}

internal static class FriendlyConfiguration
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["APP_NAME"] = "Caravel:Name", ["APP_ENV"] = "Caravel:Environment",
        ["DB_CONNECTION"] = "Database:Driver", ["DB_DATABASE"] = "Database:Database",
        ["DB_HOST"] = "Database:Host", ["DB_PORT"] = "Database:Port",
        ["DB_USERNAME"] = "Database:Username", ["DB_PASSWORD"] = "Database:Password",
        ["CACHE_DRIVER"] = "Cache:Driver", ["QUEUE_DRIVER"] = "Queue:Driver",
        ["STORAGE_DRIVER"] = "Storage:Driver", ["MAIL_DRIVER"] = "Mail:Driver"
    };

    internal static void Map(IDictionary<string, string?> data)
    {
        foreach (var (name, key) in Names)
            if (!data.ContainsKey(key) && data.TryGetValue(name, out var value)) data[key] = value;
    }
}

internal sealed class FriendlyEnvironmentSource(string? prefix) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new FriendlyEnvironmentProvider(prefix);
}

internal sealed class FriendlyEnvironmentProvider(string? prefix) : EnvironmentVariablesConfigurationProvider(prefix)
{
    public override void Load()
    {
        base.Load();
        FriendlyConfiguration.Map(Data);
    }
}

internal sealed class FriendlyCommandLineSource(CommandLineConfigurationSource source) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new FriendlyCommandLineProvider(source.Args ?? [], source.SwitchMappings);
}

internal sealed class FriendlyCommandLineProvider(IEnumerable<string> args, IDictionary<string, string>? mappings)
    : CommandLineConfigurationProvider(args, mappings)
{
    public override void Load()
    {
        base.Load();
        FriendlyConfiguration.Map(Data);
    }
}

public sealed class CaravelEnvironmentSource : FileConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        ResolveFileProvider();
        EnsureDefaults(builder);
        if (FileProvider is not PhysicalFileProvider physical)
            return new CaravelEnvironmentProvider(this);
        // Default physical providers hide dot-prefixed files, including .env.
        var files = new PhysicalFileProvider(physical.Root, ExclusionFilters.None);
        var source = new CaravelEnvironmentSource
        {
            Path = Path ?? throw new ArgumentException("An environment file path is required."), Optional = Optional, ReloadOnChange = ReloadOnChange,
            ReloadDelay = ReloadDelay, OnLoadException = OnLoadException, FileProvider = files
        };
        return new CaravelEnvironmentProvider(source, files);
    }
}

public sealed class CaravelEnvironmentProvider(CaravelEnvironmentSource source, IDisposable? ownedFiles = null) : FileConfigurationProvider(source)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) ownedFiles?.Dispose();
    }

    public override void Load(Stream stream)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var lineNumber = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var equals = line.IndexOf('=');
            if (equals <= 0) throw InvalidLine(lineNumber);
            var key = line[..equals].Trim();
            if (key.Length == 0 || !key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or ':')
                || char.IsAsciiDigit(key[0])) throw InvalidLine(lineNumber);
            var value = line[(equals + 1)..].Trim();
            if (value.Length > 0 && value[0] is '\'' or '"')
            {
                var quote = value[0];
                var text = new StringBuilder();
                var closed = false;
                for (var index = 1; index < value.Length; index++)
                {
                    var character = value[index];
                    if (character == quote)
                    {
                        var suffix = value[(index + 1)..].TrimStart();
                        if (suffix.Length != 0 && !suffix.StartsWith('#')) throw InvalidLine(lineNumber);
                        closed = true;
                        break;
                    }
                    if (character == '\\' && quote == '"' && index + 1 < value.Length)
                    {
                        var escaped = value[++index];
                        if (escaped is not ('n' or 'r' or 't' or '\\' or '"')) text.Append('\\');
                        character = escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped };
                    }
                    text.Append(character);
                }
                if (!closed) throw InvalidLine(lineNumber);
                value = text.ToString();
            }
            else
            {
                for (var index = 0; index < value.Length; index++)
                    if (value[index] == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
                    {
                        value = value[..index].TrimEnd();
                        break;
                    }
            }
            values[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }
        FriendlyConfiguration.Map(values);
        Data = values;
    }

    // Values may contain secrets: errors identify the line, never echo its contents.
    private static FormatException InvalidLine(int line) => new($"Invalid .env syntax at line {line}.");
}
