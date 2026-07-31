using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CodingAgents.Server.Services;

/// <summary>
/// Issues and validates short-lived session tokens handed out after a successful password
/// login. A token lets a client re-authenticate its hub connection after an automatic
/// reconnect without holding on to the password.
/// </summary>
public class TokenStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    // Artifact tokens travel in image/download URLs, so they are deliberately shorter-lived
    // than session tokens to bound the impact of a URL leaking (history, logs, referrer).
    private static readonly TimeSpan ArtifactLifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, DateTime> _tokens = new();
    private readonly ConcurrentDictionary<string, DateTime> _artifactTokens = new();

    public string Issue() => Issue(_tokens, Lifetime);

    public bool Validate(string? token) => Validate(_tokens, token);

    /// <summary>
    /// Issues a capability token used only to fetch files under /artifacts. It is separate
    /// from the session token so a leaked image URL cannot be replayed against the hub.
    /// </summary>
    public string IssueArtifactToken() => Issue(_artifactTokens, ArtifactLifetime);

    public bool ValidateArtifactToken(string? token) => Validate(_artifactTokens, token);

    /// <summary>Invalidates every issued token (used when the password changes).</summary>
    public void RevokeAll()
    {
        _tokens.Clear();
        _artifactTokens.Clear();
    }

    private static string Issue(ConcurrentDictionary<string, DateTime> store, TimeSpan lifetime)
    {
        Prune(store);
        // URL-safe so the token can be used as a query-string value unescaped.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        store[token] = DateTime.UtcNow.Add(lifetime);
        return token;
    }

    private static bool Validate(ConcurrentDictionary<string, DateTime> store, string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!store.TryGetValue(token, out var expiry)) return false;

        if (expiry < DateTime.UtcNow)
        {
            store.TryRemove(token, out _);
            return false;
        }
        return true;
    }

    private static void Prune(ConcurrentDictionary<string, DateTime> store)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in store)
        {
            if (kv.Value < now) store.TryRemove(kv.Key, out _);
        }
    }
}
