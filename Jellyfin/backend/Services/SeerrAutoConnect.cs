namespace Moonfin.Server.Services;

/// <summary>
/// Decides whether a Status call may connect its caller to Seerr through Quick Connect, for
/// servers that turn on AutoConnectSeerrViaQuickConnect. It only ever acts for the caller, takes
/// the Seerr username from the caller's server account rather than the request, and skips admins
/// so a compromised Seerr can never silently receive an admin's Jellyfin token.
/// </summary>
public class SeerrAutoConnect
{
    /// <summary>
    /// How long a user waits after a failed or refused attempt. Fixed on purpose: every attempt
    /// approves a Jellyfin Quick Connect code, which leaves a device and token behind, before
    /// Seerr can refuse (a user not imported in Seerr, for example).
    /// </summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly IUserDirectory _users;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<Guid, DateTimeOffset> _blockedUntil = new();
    private readonly object _gate = new();

    public SeerrAutoConnect(IUserDirectory users)
        : this(users, () => DateTimeOffset.UtcNow)
    {
    }

    internal SeerrAutoConnect(IUserDirectory users, Func<DateTimeOffset> now)
    {
        _users = users;
        _now = now;
    }

    /// <summary>
    /// Connects the caller when allowed, through <paramref name="connect"/>, which receives only
    /// the caller's id and server-side username. True when a Seerr session was created.
    /// </summary>
    public async Task<bool> TryConnectAsync(
        bool enabled,
        Guid? callerId,
        bool callerIsElevated,
        Func<Guid, string, Task<SeerrAuthResult?>> connect)
    {
        if (!enabled || callerId is not { } userId || userId == Guid.Empty || callerIsElevated)
        {
            return false;
        }

        // Claimed before anything else, so parallel Status calls from several clients start one
        // attempt between them instead of one each, and a blocked user costs no user lookup.
        lock (_gate)
        {
            if (_blockedUntil.TryGetValue(userId, out var until) && _now() < until)
            {
                return false;
            }

            _blockedUntil[userId] = _now() + Cooldown;
        }

        var user = _users.Users().FirstOrDefault(u => u.Id == userId);
        if (user == null || user.IsAdministrator || string.IsNullOrEmpty(user.Name))
        {
            return false;
        }

        SeerrAuthResult? result;
        try
        {
            result = await connect(userId, user.Name).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = null;
        }

        if (result?.Success != true)
        {
            return false;
        }

        lock (_gate)
        {
            _blockedUntil.Remove(userId);
        }

        return true;
    }
}
