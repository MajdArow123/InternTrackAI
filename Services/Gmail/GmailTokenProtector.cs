using Microsoft.AspNetCore.DataProtection;

namespace InternTrackAI.Services.Gmail;

/// <summary>
/// Encrypts Google OAuth tokens at rest with ASP.NET Data Protection. The keys live in the
/// DataProtectionKeys table (see Program.cs), so ciphertext survives redeploys while the raw
/// tokens never touch the database. A token that no longer unprotects (key ring reset) is treated
/// as a lost connection rather than an error: <see cref="Unprotect"/> returns null.
/// </summary>
public class GmailTokenProtector
{
    private const string Purpose = "InternTrackAI.GmailConnection.Tokens.v1";
    private readonly IDataProtector _protector;

    public GmailTokenProtector(IDataProtectionProvider provider) => _protector = provider.CreateProtector(Purpose);

    public string Protect(string token) => _protector.Protect(token);

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return _protector.Unprotect(ciphertext); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
