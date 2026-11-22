using System.Text.Json;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// A proxied Seerr call that finds no session, or finds Seerr no longer knows it, can connect
/// the caller under the same policy as the Status call and send the request again, once. These
/// pin that it never loops and never sends a POST or PUT twice.
/// </summary>
public class SeerrAutoConnectProxyTests
{
    private static readonly DirectoryUser Kid = new(Guid.NewGuid(), "kid", null, false);
    private static readonly DirectoryUser Admin = new(Guid.NewGuid(), "admin", null, true);

    private sealed class FakeUsers : IUserDirectory
    {
        private readonly DirectoryUser[] _users;
        public FakeUsers(params DirectoryUser[] users) => _users = users;
        public IReadOnlyList<DirectoryUser> Users() => _users;
    }

    /// <summary>Answers each send with the next response in line, repeating the last.</summary>
    private sealed class FakeSeerr
    {
        private readonly Queue<SeerrProxyResponse> _answers;
        public int Sends { get; private set; }

        public FakeSeerr(params SeerrProxyResponse[] answers) => _answers = new Queue<SeerrProxyResponse>(answers);

        public Task<SeerrProxyResponse> Send()
        {
            Sends++;
            return Task.FromResult(_answers.Count > 1 ? _answers.Dequeue() : _answers.Peek());
        }
    }

    private sealed class FakeConnect
    {
        public List<(Guid UserId, string Username)> Calls { get; } = new();
        public bool Succeed { get; set; } = true;

        public Task<SeerrAuthResult?> Invoke(Guid userId, string username)
        {
            Calls.Add((userId, username));
            return Task.FromResult<SeerrAuthResult?>(new SeerrAuthResult { Success = Succeed });
        }
    }

    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private SeerrAutoConnect Create(params DirectoryUser[] users) => new(new FakeUsers(users), () => _now);

    private static SeerrProxyResponse Refusal(string code) => new()
    {
        StatusCode = 401,
        Body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Not authenticated with Seerr", code }),
    };

    private static SeerrProxyResponse NoSession() => Refusal("NO_SESSION");

    private static SeerrProxyResponse Expired() => Refusal("SESSION_EXPIRED");

    private static SeerrProxyResponse Ok() => new() { StatusCode = 200, Body = "{}"u8.ToArray() };

    private static Task<SeerrProxyResponse> Proxy(
        SeerrAutoConnect policy, HttpMethod method, FakeSeerr seerr, FakeConnect connect,
        Guid? caller = null, bool enabled = true, bool elevated = false) =>
        policy.ProxyAsync(enabled, caller ?? Kid.Id, elevated, method, seerr.Send, connect.Invoke);

    // (a) No stored session: the request never reached Seerr, so any method is sent after a connect.

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task NoSessionConnectsTheCallerAndForwardsTheRequest(string method)
    {
        var seerr = new FakeSeerr(NoSession(), Ok());
        var connect = new FakeConnect();

        var result = await Proxy(Create(Kid), new HttpMethod(method), seerr, connect);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, seerr.Sends);
        Assert.Equal(new[] { (Kid.Id, "kid") }, connect.Calls);
    }

    [Fact]
    public async Task WithTheOptionOffNoSessionIsPassedStraightThrough()
    {
        var seerr = new FakeSeerr(NoSession(), Ok());
        var connect = new FakeConnect();

        var result = await Proxy(Create(Kid), HttpMethod.Get, seerr, connect, enabled: false);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal(1, seerr.Sends);
        Assert.Empty(connect.Calls);
    }

    [Fact]
    public async Task AnAdminWithNoSessionIsNeverConnected()
    {
        var seerr = new FakeSeerr(NoSession(), Ok());
        var connect = new FakeConnect();
        var policy = Create(Admin);

        Assert.Equal(401, (await Proxy(policy, HttpMethod.Get, seerr, connect, caller: Admin.Id)).StatusCode);
        Assert.Equal(401, (await Proxy(Create(Kid), HttpMethod.Get, new FakeSeerr(NoSession()), connect, elevated: true)).StatusCode);

        Assert.Equal(1, seerr.Sends);
        Assert.Empty(connect.Calls);
    }

    [Fact]
    public async Task AFailedConnectReturnsTheRefusalAndTheCooldownCoversLaterCalls()
    {
        var seerr = new FakeSeerr(NoSession());
        var connect = new FakeConnect { Succeed = false };
        var policy = Create(Kid);

        Assert.Equal(401, (await Proxy(policy, HttpMethod.Get, seerr, connect)).StatusCode);
        Assert.Equal(401, (await Proxy(policy, HttpMethod.Get, seerr, connect)).StatusCode);

        Assert.Equal(2, seerr.Sends);
        Assert.Single(connect.Calls);
    }

    [Fact]
    public async Task TheProxyAndTheStatusCallShareOneCooldown()
    {
        var connect = new FakeConnect { Succeed = false };
        var policy = Create(Kid);

        Assert.False(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));
        await Proxy(policy, HttpMethod.Get, new FakeSeerr(NoSession()), connect);

        Assert.Single(connect.Calls);
    }

    [Fact]
    public async Task AMissingOrEmptyCallerIsNeverConnected()
    {
        var connect = new FakeConnect();
        var policy = Create(Kid);

        await policy.ProxyAsync(true, null, false, HttpMethod.Get, new FakeSeerr(NoSession()).Send, connect.Invoke);
        await policy.ProxyAsync(true, Guid.Empty, false, HttpMethod.Get, new FakeSeerr(NoSession()).Send, connect.Invoke);

        Assert.Empty(connect.Calls);
    }

    // (b) Seerr no longer knows the session (already cleared by the session service): reconnect
    // once and retry once, but only for requests that are safe to send twice.

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task ADeadSessionIsReconnectedAndAnIdempotentRequestRetriedOnce(string method)
    {
        var seerr = new FakeSeerr(Expired(), Ok());
        var connect = new FakeConnect();

        var result = await Proxy(Create(Kid), new HttpMethod(method), seerr, connect);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, seerr.Sends);
        Assert.Single(connect.Calls);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task ADeadSessionIsReconnectedButAPostOrPutIsNotSentAgain(string method)
    {
        var seerr = new FakeSeerr(Expired(), Ok());
        var connect = new FakeConnect();

        var result = await Proxy(Create(Kid), new HttpMethod(method), seerr, connect);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal(1, seerr.Sends);
        Assert.Single(connect.Calls);
    }

    [Fact]
    public async Task ASessionThatDiesAgainOnTheRetryIsReturnedWithoutAnotherTry()
    {
        var seerr = new FakeSeerr(Expired(), Expired(), Ok());
        var connect = new FakeConnect();

        var result = await Proxy(Create(Kid), HttpMethod.Get, seerr, connect);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal(2, seerr.Sends);
        Assert.Single(connect.Calls);
    }

    [Fact]
    public async Task NoSessionThenADeadOneOnTheForwardStopsAfterTwoSends()
    {
        var seerr = new FakeSeerr(NoSession(), Expired(), Ok());
        var connect = new FakeConnect();

        var result = await Proxy(Create(Kid), HttpMethod.Get, seerr, connect);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal(2, seerr.Sends);
        Assert.Single(connect.Calls);
    }

    [Fact]
    public async Task AnAdminsDeadSessionIsNotReconnected()
    {
        var seerr = new FakeSeerr(Expired(), Ok());
        var connect = new FakeConnect();

        var result = await Proxy(Create(Admin), HttpMethod.Get, seerr, connect, caller: Admin.Id);

        Assert.Equal(401, result.StatusCode);
        Assert.Equal(1, seerr.Sends);
        Assert.Empty(connect.Calls);
    }

    [Fact]
    public async Task OtherRefusalsArePassedThroughUntouched()
    {
        // A 401 or 403 Seerr gives on a session it still knows is a permission answer, not a lost session.
        var forbidden = new SeerrProxyResponse { StatusCode = 403, Body = """{"message":"no"}"""u8.ToArray() };
        var plain401 = new SeerrProxyResponse { StatusCode = 401, Body = """{"message":"no"}"""u8.ToArray() };
        var unparsable = new SeerrProxyResponse { StatusCode = 401, Body = "<html>"u8.ToArray() };
        var connect = new FakeConnect();
        var policy = Create(Kid);

        foreach (var answer in new[] { forbidden, plain401, unparsable, new SeerrProxyResponse { StatusCode = 401 } })
        {
            var seerr = new FakeSeerr(answer, Ok());
            Assert.Same(answer, await Proxy(policy, HttpMethod.Get, seerr, connect));
            Assert.Equal(1, seerr.Sends);
        }

        Assert.Empty(connect.Calls);
    }
}
