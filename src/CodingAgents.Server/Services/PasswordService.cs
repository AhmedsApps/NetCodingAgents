using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using CodingAgents.Shared;
using CodingAgents.Server.Data;

namespace CodingAgents.Server.Services;

/// <summary>
/// Manages the single app access password. Stores only a PBKDF2 hash + per-credential salt;
/// the plaintext password is never persisted and never sent to clients.
/// </summary>
public class PasswordService
{
    // The password the app ships with. The user is expected to change it after first login.
    public const string DefaultPassword = "admin";

    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private static readonly SemaphoreSlim _initLock = new(1, 1);

    private static (string hash, string salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool Verify(string password, string storedHash, string storedSalt)
    {
        try
        {
            var salt = Convert.FromBase64String(storedSalt);
            var expected = Convert.FromBase64String(storedHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            // Constant-time comparison to avoid leaking information through timing.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the credential row, seeding it with the default password if absent.</summary>
    private static async Task<AppCredential> GetOrCreateAsync(ChatDbContext db)
    {
        var cred = await db.AppCredentials.OrderBy(c => c.Id).FirstOrDefaultAsync();
        if (cred != null) return cred;

        await _initLock.WaitAsync();
        try
        {
            cred = await db.AppCredentials.OrderBy(c => c.Id).FirstOrDefaultAsync();
            if (cred == null)
            {
                var (hash, salt) = HashPassword(DefaultPassword);
                cred = new AppCredential { Id = 1, PasswordHash = hash, PasswordSalt = salt };
                db.AppCredentials.Add(cred);
                await db.SaveChangesAsync();
            }
            return cred;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> VerifyAsync(ChatDbContext db, string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        var cred = await GetOrCreateAsync(db);
        return Verify(password, cred.PasswordHash, cred.PasswordSalt);
    }

    /// <summary>Changes the password after verifying the current one. Returns an error message, or null on success.</summary>
    public async Task<string?> ChangeAsync(ChatDbContext db, string currentPassword, string newPassword)
    {
        var cred = await GetOrCreateAsync(db);

        if (!Verify(currentPassword, cred.PasswordHash, cred.PasswordSalt))
        {
            return "The current password is incorrect.";
        }
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
        {
            return "The new password must be at least 4 characters.";
        }

        var (hash, salt) = HashPassword(newPassword);
        cred.PasswordHash = hash;
        cred.PasswordSalt = salt;
        await db.SaveChangesAsync();
        return null;
    }

    /// <summary>True while the password is still the shipped default (used to nudge the user).</summary>
    public async Task<bool> IsDefaultAsync(ChatDbContext db)
    {
        var cred = await GetOrCreateAsync(db);
        return Verify(DefaultPassword, cred.PasswordHash, cred.PasswordSalt);
    }
}
