using Microsoft.AspNetCore.SignalR;

namespace CodingAgents.Server.Hubs;

/// <summary>
/// Enforces authentication on every hub method call. Without this, any client that can reach
/// the server could invoke hub methods directly and drive the local agent worker — the UI
/// login screen alone is not a security boundary.
/// </summary>
/// <remarks>
/// Authentication state lives in <see cref="HubCallerContext.Items"/>, which is per-connection
/// and server-side, so a client cannot forge it. After an automatic reconnect the connection is
/// new and unauthenticated, so the client must call Authenticate again with its token.
/// </remarks>
public class AuthHubFilter : IHubFilter
{
    // Key used to mark a connection as authenticated.
    public const string AuthenticatedKey = "__authenticated";

    // The only methods callable before authenticating.
    private static readonly HashSet<string> AnonymousMethods = new(StringComparer.Ordinal)
    {
        nameof(ChatHub.Login),
        nameof(ChatHub.Authenticate),
        nameof(ChatHub.RegisterWorker)
    };

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var methodName = invocationContext.HubMethodName;

        if (!AnonymousMethods.Contains(methodName) && !IsAuthenticated(invocationContext.Context))
        {
            throw new HubException($"Not authenticated. Sign in before calling '{methodName}'.");
        }

        return await next(invocationContext);
    }

    public static bool IsAuthenticated(HubCallerContext context)
        => context.Items.TryGetValue(AuthenticatedKey, out var value) && value is true;

    public static void MarkAuthenticated(HubCallerContext context)
        => context.Items[AuthenticatedKey] = true;
}
