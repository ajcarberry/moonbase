using System.Text.Json;

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

    /// <summary>
    /// Sends a proxied Seerr request through <paramref name="send"/>. When it comes back with no
    /// session (never sent to Seerr) or an expired one (Seerr no longer knew it, and the session
    /// service already cleared it), connects the caller under the same policy as the Status call
    /// and sends it again, once. An expired GET or DELETE is retried. An expired POST or PUT is
    /// not: Seerr refused it on authentication, but sending a write twice is not worth the risk,
    /// so it reconnects and returns the refusal for the client to try again.
    /// </summary>
    public async Task<SeerrProxyResponse> ProxyAsync(
        bool enabled,
        Guid? callerId,
        bool callerIsElevated,
        HttpMethod method,
        Func<Task<SeerrProxyResponse>> send,
        Func<Guid, string, Task<SeerrAuthResult?>> connect)
    {
        var first = await send().ConfigureAwait(false);
        var code = LostSessionCode(first);
        if (code == null || !await TryConnectAsync(enabled, callerId, callerIsElevated, connect).ConfigureAwait(false))
        {
            return first;
        }

        var safeToSendAgain = code == NoSession || method == HttpMethod.Get || method == HttpMethod.Delete;
        return safeToSendAgain ? await send().ConfigureAwait(false) : first;
    }

    private const string NoSession = "NO_SESSION";
    private const string SessionExpired = "SESSION_EXPIRED";

    // The 401 answers SeerrSessionService.ProxyRequestAsync gives for a missing session and for
    // one Seerr confirmed dead. Every other refusal is Seerr's own and passes through.
    private static string? LostSessionCode(SeerrProxyResponse response)
    {
        if (response.StatusCode != 401 || response.Body == null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var code = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var prop)
                && prop.ValueKind == JsonValueKind.String
                    ? prop.GetString()
                    : null;
            return code is NoSession or SessionExpired ? code : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
