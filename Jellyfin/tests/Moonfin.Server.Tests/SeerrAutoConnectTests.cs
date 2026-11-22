using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// The Status call can connect a signed-in user to Seerr through Quick Connect when the admin
/// turns AutoConnectSeerrViaQuickConnect on. These pin who it acts for, who it skips, and how
/// often it may try, since every attempt approves a Jellyfin Quick Connect code.
/// </summary>
public class SeerrAutoConnectTests
{
    private static readonly DirectoryUser Kid = new(Guid.NewGuid(), "kid", null, false);
    private static readonly DirectoryUser Parent = new(Guid.NewGuid(), "parent", null, false);
    private static readonly DirectoryUser Admin = new(Guid.NewGuid(), "admin", null, true);

    private sealed class FakeUsers : IUserDirectory
    {
        private readonly DirectoryUser[] _users;
        public int Calls { get; private set; }

        public FakeUsers(params DirectoryUser[] users) => _users = users;

        public IReadOnlyList<DirectoryUser> Users()
        {
            Calls++;
            return _users;
        }
    }

    /// <summary>Records every connect the policy asks for and answers with a fixed result.</summary>
    private sealed class FakeConnect
    {
        public List<(Guid UserId, string Username)> Calls { get; } = new();
        public Func<SeerrAuthResult?> Answer { get; set; } = () => new SeerrAuthResult { Success = true };

        public Task<SeerrAuthResult?> Invoke(Guid userId, string username)
        {
            Calls.Add((userId, username));
            return Task.FromResult(Answer());
        }
    }

    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private SeerrAutoConnect Create(FakeUsers users) => new(users, () => _now);

    private static SeerrAuthResult Refused() => new() { Success = false, ErrorCode = "authenticate_failed" };

    [Fact]
    public void TheOptionIsOffByDefault()
    {
        Assert.False(new PluginConfiguration().AutoConnectSeerrViaQuickConnect);
    }

    [Fact]
    public async Task WithTheOptionOnACallerWithNoSessionIsConnected()
    {
        var connect = new FakeConnect();
        var policy = Create(new FakeUsers(Kid, Parent));

        var connected = await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke);

        Assert.True(connected);
        Assert.Equal(new[] { (Kid.Id, "kid") }, connect.Calls);
    }

    [Fact]
    public async Task WithTheOptionOffNothingIsTriedOrLookedUp()
    {
        var connect = new FakeConnect();
        var users = new FakeUsers(Kid);
        var policy = Create(users);

        Assert.False(await policy.TryConnectAsync(false, Kid.Id, false, connect.Invoke));

        Assert.Empty(connect.Calls);
        Assert.Equal(0, users.Calls);
    }

    [Fact]
    public async Task AnAdministratorIsNeverConnected()
    {
        var connect = new FakeConnect();
        var policy = Create(new FakeUsers(Admin, Kid));

        Assert.False(await policy.TryConnectAsync(true, Admin.Id, false, connect.Invoke));

        Assert.Empty(connect.Calls);
    }

    [Fact]
    public async Task ACallerWhoseClaimsCarryTheAdminRoleOrAnApiKeyIsNeverConnected()
    {
        // The directory says not an admin, but the request's own claims say elevated: skip anyway.
        var connect = new FakeConnect();
        var policy = Create(new FakeUsers(Kid));

        Assert.False(await policy.TryConnectAsync(true, Kid.Id, true, connect.Invoke));

        Assert.Empty(connect.Calls);
    }

    [Fact]
    public async Task AFailedAttemptBlocksTheNextOneUntilTheCooldownEnds()
    {
        var connect = new FakeConnect { Answer = Refused };
        var policy = Create(new FakeUsers(Kid));

        Assert.False(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));
        _now += SeerrAutoConnect.Cooldown - TimeSpan.FromSeconds(1);
        Assert.False(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));
        Assert.Single(connect.Calls);

        _now += TimeSpan.FromSeconds(2);
        connect.Answer = () => new SeerrAuthResult { Success = true };
        Assert.True(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));
        Assert.Equal(2, connect.Calls.Count);
    }

    [Fact]
    public async Task ANullResultOrAThrowCountsAsAFailureAndStartsTheCooldown()
    {
        var connect = new FakeConnect { Answer = () => null };
        var policy = Create(new FakeUsers(Kid, Parent));

        Assert.False(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));
        Assert.False(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));
        Assert.Single(connect.Calls);

        connect.Answer = () => throw new HttpRequestException("Seerr is down");
        Assert.False(await policy.TryConnectAsync(true, Parent.Id, false, connect.Invoke));
        Assert.False(await policy.TryConnectAsync(true, Parent.Id, false, connect.Invoke));
        Assert.Equal(2, connect.Calls.Count);
    }

    [Fact]
    public async Task TheCooldownIsPerUser()
    {
        var connect = new FakeConnect { Answer = Refused };
        var policy = Create(new FakeUsers(Kid, Parent));

        await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke);
        await policy.TryConnectAsync(true, Parent.Id, false, connect.Invoke);

        Assert.Equal(new[] { Kid.Id, Parent.Id }, connect.Calls.Select(c => c.UserId));
    }

    [Fact]
    public async Task ASecondStatusCallWhileAnAttemptIsRunningDoesNotStartAnother()
    {
        var gate = new TaskCompletionSource<SeerrAuthResult?>();
        var calls = 0;
        var policy = Create(new FakeUsers(Kid));
        Task<SeerrAuthResult?> Slow(Guid _, string __)
        {
            calls++;
            return gate.Task;
        }

        var first = policy.TryConnectAsync(true, Kid.Id, false, Slow);
        Assert.False(await policy.TryConnectAsync(true, Kid.Id, false, Slow));
        gate.SetResult(new SeerrAuthResult { Success = true });

        Assert.True(await first);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ASuccessLetsTheNextLostSessionReconnectAtOnce()
    {
        var connect = new FakeConnect();
        var policy = Create(new FakeUsers(Kid));

        Assert.True(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));
        Assert.True(await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke));

        Assert.Equal(2, connect.Calls.Count);
    }

    [Fact]
    public async Task AMissingOrEmptyUserClaimNeverConnects()
    {
        var connect = new FakeConnect();
        var users = new FakeUsers(Kid);
        var policy = Create(users);

        Assert.False(await policy.TryConnectAsync(true, null, false, connect.Invoke));
        Assert.False(await policy.TryConnectAsync(true, Guid.Empty, false, connect.Invoke));

        Assert.Empty(connect.Calls);
        Assert.Equal(0, users.Calls);
    }

    [Fact]
    public async Task ACallerWhoIsNotAServerUserNeverActsAsAnybodyElse()
    {
        var connect = new FakeConnect();
        var policy = Create(new FakeUsers(Kid, Parent));

        Assert.False(await policy.TryConnectAsync(true, Guid.NewGuid(), false, connect.Invoke));

        Assert.Empty(connect.Calls);
    }

    [Fact]
    public async Task OnlyTheCallersIdAndServerSideNameAreHandedToTheConnect()
    {
        var connect = new FakeConnect();
        var policy = Create(new FakeUsers(Parent, Kid));

        await policy.TryConnectAsync(true, Kid.Id, false, connect.Invoke);

        var call = Assert.Single(connect.Calls);
        Assert.Equal(Kid.Id, call.UserId);
        Assert.Equal("kid", call.Username);
    }

    [Fact]
    public async Task AUserWithNoNameIsNotConnected()
    {
        var nameless = new DirectoryUser(Guid.NewGuid(), "", null, false);
        var connect = new FakeConnect();
        var policy = Create(new FakeUsers(nameless));

        Assert.False(await policy.TryConnectAsync(true, nameless.Id, false, connect.Invoke));

        Assert.Empty(connect.Calls);
    }
}
