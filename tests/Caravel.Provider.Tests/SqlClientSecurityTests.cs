using System.Net.Security;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Caravel.Provider.Tests;

// Exercises the installed SqlClient assembly, not a copy of its implementation.
// Connections remain unopened. Policy flags are synthetic, not a TLS handshake claim.
public sealed class SqlClientSecurityTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public SqlClientSecurityTests(ITestOutputHelper output)
    {
        var assembly = typeof(SqlConnection).Assembly;
        output.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; CLR: {Environment.Version}; OS: {RuntimeInformation.OSDescription}; Architecture: {RuntimeInformation.ProcessArchitecture}");
        output.WriteLine($"Loaded SqlClient: {assembly.GetName().Name}; AssemblyVersion: {assembly.GetName().Version}; FileVersion: {assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version}; InformationalVersion: {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
    }

    [Theory]
    [InlineData(null, SslPolicyErrors.None, true)]
    [InlineData("", SslPolicyErrors.None, true)]
    [InlineData("server.example", SslPolicyErrors.None, true)]
    [InlineData("SERVER.EXAMPLE", SslPolicyErrors.None, true)]
    [InlineData("alias.example", SslPolicyErrors.None, true)]
    [InlineData("other.example", SslPolicyErrors.None, false)]
    [InlineData("server.example", SslPolicyErrors.RemoteCertificateNameMismatch, true)]
    [InlineData("other.example", SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    public void ExplicitCertificateNameIsEnforced(string? name, SslPolicyErrors errors, bool accepted)
    {
        using var certificate = Certificate();
        if (accepted) Assert.True(Validate(certificate, name, null, errors));
        else Assert.Throws<AuthenticationException>(() => Validate(certificate, name, null, errors));
    }

    [Fact]
    public void ExplicitCertificateNameRejectsMissingCertificate()
        => Assert.Throws<AuthenticationException>(() => Validate(null, "server.example", null, SslPolicyErrors.None));

    // Preservation control: certificate-pin fail-closed behavior predates 6.1.8.
    [Fact]
    public void CertificatePinRemainsExactAndFailClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "caravel-sqlclient-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var certificate = Certificate();
            using var other = Certificate();
            var pin = Path.Combine(directory, "pin.cer");
            File.WriteAllBytes(pin, certificate.Export(X509ContentType.Cert));
            Assert.True(Validate(certificate, "other.example", pin, SslPolicyErrors.None));
            Assert.Throws<AuthenticationException>(() => Validate(other, null, pin, SslPolicyErrors.None));
            Assert.Throws<AuthenticationException>(() => Validate(certificate, null, Path.Combine(directory, "missing.cer"), SslPolicyErrors.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Assert.False(Directory.Exists(directory));
        }
    }

    [Theory]
    [InlineData(SqlConnectionAttestationProtocol.None, SqlConnectionAttestationProtocol.HGS)]
    [InlineData(SqlConnectionAttestationProtocol.None, SqlConnectionAttestationProtocol.AAS)]
    [InlineData(SqlConnectionAttestationProtocol.HGS, SqlConnectionAttestationProtocol.None)]
    [InlineData(SqlConnectionAttestationProtocol.HGS, SqlConnectionAttestationProtocol.AAS)]
    [InlineData(SqlConnectionAttestationProtocol.AAS, SqlConnectionAttestationProtocol.None)]
    [InlineData(SqlConnectionAttestationProtocol.AAS, SqlConnectionAttestationProtocol.HGS)]
    public void EnclaveCacheIsolatesAttestationPolicies(SqlConnectionAttestationProtocol first, SqlConnectionAttestationProtocol second)
    {
        var cache = Cache();
        var a = Parameters(first);
        var b = Parameters(second);
        var sessionA = Create(cache, a, 101);
        Assert.Null(Read(cache, b));
        Invoke(cache, "InvalidateSession", b, sessionA);
        Assert.Same(sessionA, Read(cache, a));
        var sessionB = Create(cache, b, 101);
        Assert.Same(sessionA, Read(cache, a));
        Assert.Same(sessionB, Read(cache, b));
        Invoke(cache, "InvalidateSession", a, sessionA);
        Assert.Null(Read(cache, a));
        Assert.Same(sessionB, Read(cache, b));
    }

    [Theory]
    [InlineData(SqlConnectionAttestationProtocol.None)]
    [InlineData(SqlConnectionAttestationProtocol.HGS)]
    [InlineData(SqlConnectionAttestationProtocol.AAS)]
    public void EnclaveCachePreservesSamePolicyReuseAndStaleInvalidation(SqlConnectionAttestationProtocol protocol)
    {
        var cache = Cache();
        var a = Parameters(protocol);
        var equivalent = Parameters(protocol, "SERVER.EXAMPLE", "DATABASE", "https://ATTESTATION.EXAMPLE");
        var stale = Create(cache, a, 101);
        var read1 = new object?[] { equivalent, 0L };
        var read2 = new object?[] { a, 0L };
        Assert.Same(stale, Invoke(cache, "GetEnclaveSession", read1));
        Assert.Same(stale, Invoke(cache, "GetEnclaveSession", read2));
        Assert.True((long)read2[1]! > (long)read1[1]!);
        var replacement = Create(cache, a, 102);
        Invoke(cache, "InvalidateSession", equivalent, stale);
        Assert.Same(replacement, Read(cache, a));
        Invoke(cache, "InvalidateSession", equivalent, replacement);
        Assert.Null(Read(cache, a));
    }

    [Fact]
    public void EnclaveCachePreservesFieldBoundaries()
    {
        var cache = Cache();
        var a = Parameters(SqlConnectionAttestationProtocol.None, "server+part", "database");
        var b = Parameters(SqlConnectionAttestationProtocol.None, "server", "part+database");
        var session = Create(cache, a, 101);
        Assert.Null(Read(cache, b));
        Invoke(cache, "InvalidateSession", b, session);
        Assert.Same(session, Read(cache, a));
    }

    private static object Parameters(SqlConnectionAttestationProtocol protocol, string server = "server.example", string database = "database", string url = "https://attestation.example")
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server, InitialCatalog = database, EnclaveAttestationUrl = url,
            AttestationProtocol = protocol, ColumnEncryptionSetting = SqlConnectionColumnEncryptionSetting.Enabled
        };
        using var connection = new SqlConnection(builder.ConnectionString);
        using var command = connection.CreateCommand();
        // The production command path carries policy into the cache identity in fixed packages.
        return Invoke(command, "GetEnclaveSessionParameters")!;
    }

    private static object Cache() => Activator.CreateInstance(typeof(SqlConnection).Assembly.GetType("Microsoft.Data.SqlClient.EnclaveSessionCache", throwOnError: true)!, nonPublic: true)!;
    private static object Create(object cache, object parameters, long id) => Invoke(cache, "CreateSession", parameters, new byte[] { 1, 2, 3 }, id, 0L)!;
    private static object? Read(object cache, object parameters) => Invoke(cache, "GetEnclaveSession", parameters, 0L);
    private static object? Invoke(object target, string name, params object?[] args)
        => Call(target.GetType().GetMethod(name, Instance) ?? throw new MissingMethodException(target.GetType().FullName, name), target, args);
    private static object? Call(MethodInfo method, object? target, object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
    private static bool Validate(X509Certificate2? certificate, string? name, string? pin, SslPolicyErrors errors)
    {
        var type = typeof(SqlConnection).Assembly.GetType("Microsoft.Data.SqlClient.ManagedSni.SniCommon", throwOnError: true)!;
        var method = type.GetMethod("ValidateSslServerCertificate", Static) ?? throw new MissingMethodException(type.FullName, "ValidateSslServerCertificate");
        return (bool)Call(method, null, new object?[] { Guid.NewGuid(), "server.example", name, certificate, pin, errors })!;
    }
    private static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=server.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("server.example");
        names.AddDnsName("alias.example");
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
}
