using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using global::Azure.Core;
using global::Azure.Core.Diagnostics;
using global::Azure.Core.Pipeline;
using Xunit;

namespace Caravel.Storage.Azure.Tests;

public class AzureTransportSecurityTests
{
    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    public async Task StreamingLogsRespectPayloadLengthAndConfiguredLimit(bool asynchronousRead, int logLimit)
    {
        var contentLogs = new ConcurrentQueue<string>();
        using var listener = new AzureEventSourceListener((written, message) =>
        {
            if (written.EventSource.Name == "Azure-Core" &&
                written.EventName?.Contains("ResponseContent", StringComparison.Ordinal) == true)
                contentLogs.Enqueue(message);
        }, EventLevel.Verbose);
        using var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekableStream("payload"u8.ToArray())) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return response;
        });
        var options = Options(handler);
        options.Diagnostics.IsLoggingContentEnabled = true;
        options.Diagnostics.LoggedContentSizeLimit = logLimit;
        var pipeline = HttpPipelineBuilder.Build(options);
        using var request = pipeline.CreateMessage();
        request.Request.Method = RequestMethod.Get;
        request.Request.Uri.Reset(new Uri("https://storage.invalid/stream-log-test"));
        request.BufferResponse = false;
        await pipeline.SendAsync(request, CancellationToken.None);

        var buffer = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("UNREAD-SYNTHETIC-BUFFER;", 16)));
        var stream = request.Response.ContentStream!;
        var length = asynchronousRead
            ? await stream.ReadAsync(buffer.AsMemory())
            : stream.Read(buffer, 0, buffer.Length);
        Assert.Equal("payload", Encoding.UTF8.GetString(buffer, 0, length));
        var expectedLog = "payload"[..Math.Min(logLimit, "payload".Length)];
        Assert.Contains(contentLogs, log => log.Contains(expectedLog, StringComparison.Ordinal));
        Assert.All(contentLogs, log =>
        {
            if (logLimit < "payload".Length) Assert.DoesNotContain("payload", log, StringComparison.Ordinal);
            Assert.DoesNotContain("UNREAD-SYNTHETIC-BUFFER", log, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("storage.invalid", true)]
    [InlineData("redirect.invalid", false)]
    public async Task ExplicitRedirectsOnlyRetainBearerTokensForTheOriginalHost(string host, bool retainToken)
    {
        using var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/start")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new Uri($"https://{host}/finish");
                return redirect;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var credential = new SyntheticCredential();
        var pipeline = HttpPipelineBuilder.Build(Options(handler),
            new BearerTokenAuthenticationPolicy(credential, "https://storage.invalid/.default"));
        using var request = pipeline.CreateMessage();
        request.Request.Method = RequestMethod.Get;
        request.Request.Uri.Reset(new Uri("https://storage.invalid/start"));
        RedirectPolicy.SetAllowAutoRedirect(request, true);
        await pipeline.SendAsync(request, CancellationToken.None);

        Assert.Equal(200, request.Response.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer synthetic-token", handler.Requests[0].Authorization);
        Assert.Equal(retainToken ? "Bearer synthetic-token" : null, handler.Requests[1].Authorization);
        Assert.Equal(1, credential.RequestCount);
    }

    private static TestOptions Options(HttpMessageHandler handler)
    {
        var options = new TestOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        return options;
    }

    private sealed class TestOptions : ClientOptions;

    private sealed record SentRequest(Uri Uri, string? Authorization);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(reply(request));
        }
    }

    private sealed class NonSeekableStream(byte[] payload) : Stream
    {
        private readonly MemoryStream content = new(payload);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            content.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            content.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) content.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class SyntheticCredential : TokenCredential
    {
        public int RequestCount { get; private set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
        {
            RequestCount++;
            return new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(context, cancellationToken));
    }
}
