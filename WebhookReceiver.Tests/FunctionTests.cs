using System.Security.Cryptography;
using System.Text;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using WebhookReceiver.Common;
using WebhookReceiver.Common.Ports;
using WebhookReceiver.Features.ReceivePaymentWebhook;

namespace WebhookReceiver.Tests;

public sealed class FunctionTests
{
    private const string Secret = "local-webhook-secret";
    private const string Header = "X-Signature";
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly RecordingQueue _queue = new RecordingQueue();

    [Fact]
    public async Task Handler_ValidSignature_Returns200AndQueuesRawBody()
    {
        string body = EventBody("evt_1", Now);
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, Sign(body, Secret)), new TestLambdaContext());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"received\":true}", response.Body);
        QueuedWebhook queued = Assert.Single(_queue.Sent);
        Assert.Equal(body, queued.Body);
        Assert.Equal("evt_1", queued.GatewayEventId);
        Assert.False(string.IsNullOrEmpty(queued.CorrelationId));
    }

    [Fact]
    public async Task Handler_WrongSignature_Returns401AndQueuesNothing()
    {
        string body = EventBody("evt_1", Now);
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, Sign(body, "another-secret")), new TestLambdaContext());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(_queue.Sent);
    }

    [Fact]
    public async Task Handler_MissingSignatureHeader_Returns401AndQueuesNothing()
    {
        string body = EventBody("evt_1", Now);
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, null), new TestLambdaContext());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(_queue.Sent);
    }

    [Fact]
    public async Task Handler_SignatureOverTamperedBody_Returns401()
    {
        string body = EventBody("evt_1", Now);
        string signature = Sign(body, Secret);
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body.Replace("100", "999"), signature), new TestLambdaContext());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(_queue.Sent);
    }

    [Fact]
    public async Task Handler_QueueRefuses_Returns500()
    {
        string body = EventBody("evt_1", Now);
        _queue.Fails = true;
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, Sign(body, Secret)), new TestLambdaContext());

        Assert.Equal(500, response.StatusCode);
    }

    [Fact]
    public async Task Handler_Base64EncodedBody_VerifiesAgainstDecodedBytes()
    {
        string body = EventBody("evt_1", Now);
        APIGatewayHttpApiV2ProxyRequest request = Request(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), Sign(body, Secret));
        request.IsBase64Encoded = true;
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(request, new TestLambdaContext());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(body, Assert.Single(_queue.Sent).Body);
    }

    [Fact]
    public async Task Handler_HeaderNameInOtherCase_IsStillFound()
    {
        string body = EventBody("evt_1", Now);
        APIGatewayHttpApiV2ProxyRequest request = Request(body, null);
        request.Headers = new Dictionary<string, string> { ["x-signature"] = Sign(body, Secret) };
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(request, new TestLambdaContext());

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Handler_SignedBodyWithoutEventId_Returns400AndQueuesNothing()
    {
        const string body = "{\"outcome\":\"success\"}";
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, Sign(body, Secret)), new TestLambdaContext());

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(_queue.Sent);
    }

    [Fact]
    public async Task Handler_EventOlderThanReplayWindow_Returns401AndQueuesNothing()
    {
        string body = EventBody("evt_1", Now.AddMinutes(-6));
        Function function = CreateFunction(TimeSpan.FromMinutes(5));

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, Sign(body, Secret)), new TestLambdaContext());

        Assert.Equal(401, response.StatusCode);
        Assert.Empty(_queue.Sent);
    }

    [Fact]
    public async Task Handler_ReplayWindowDisabled_AcceptsOldEvent()
    {
        string body = EventBody("evt_1", Now.AddDays(-1));
        Function function = CreateFunction(TimeSpan.Zero);

        APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, Sign(body, Secret)), new TestLambdaContext());

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Handler_SameEventFiveTimes_Returns200EachTime()
    {
        string body = EventBody("evt_1", Now);
        Function function = CreateFunction(TimeSpan.Zero);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            APIGatewayHttpApiV2ProxyResponse response = await function.Handler(Request(body, Sign(body, Secret)), new TestLambdaContext());
            Assert.Equal(200, response.StatusCode);
        }

        Assert.Equal(5, _queue.Sent.Count);
        Assert.All(_queue.Sent, sent => Assert.Equal("evt_1", sent.GatewayEventId));
    }

    private Function CreateFunction(TimeSpan replayWindow)
    {
        var clock = new FixedClock(Now);
        var handler = new ReceivePaymentWebhookHandler(
            new FixedSecretSource(Secret),
            _queue,
            new ReplayGuard(clock, replayWindow),
            NullLogger<ReceivePaymentWebhookHandler>.Instance);
        return new Function(handler, Header, NullLogger<Function>.Instance);
    }

    private static string EventBody(string eventId, DateTimeOffset occurredAt)
    {
        string timestamp = occurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return "{\"gatewayEventId\":\"" + eventId + "\",\"gatewayRef\":\"ref_1\",\"outcome\":\"success\",\"amount\":100,\"occurredAt\":\"" + timestamp + "\"}";
    }

    private static string Sign(string body, string secret)
    {
        byte[] hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static APIGatewayHttpApiV2ProxyRequest Request(string body, string? signature)
    {
        var headers = new Dictionary<string, string>();
        if (signature != null)
        {
            headers[Header] = signature;
        }

        return new APIGatewayHttpApiV2ProxyRequest { Body = body, Headers = headers };
    }
}
