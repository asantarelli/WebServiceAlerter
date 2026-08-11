using System.Security.Cryptography;
using System.Text;

namespace WebServiceAlerter.Security;

/// <summary>
/// DPAPI wrapper for the SMTP password, in LocalMachine scope.
///
/// The threat being addressed is not eavesdropping on alert mail — it is somebody lifting the
/// credentials of a mailbox on our own domain off a client PC and sending phishing that appears
/// to come from us. LocalMachine scope ties the blob to the machine that produced it, so copying
/// appsettings.json somewhere else yields nothing usable.
///
/// This does not make the secret safe from an administrator on that same machine: the service
/// runs as LocalSystem and can decrypt it, so anyone with equivalent rights can too. It raises
/// the cost from casual to deliberate, which for a file sitting on dozens of client desktops is
/// the difference that matters.
/// </summary>
public static class PasswordProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WebServiceAlerter.Smtp.v1");

    public static string Protect(string plainText)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plainText),
            Entropy,
            DataProtectionScope.LocalMachine);

        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Returns null when the blob is malformed or was produced on another machine.</summary>
    public static string? TryUnprotect(string protectedBase64)
    {
        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedBase64),
                Entropy,
                DataProtectionScope.LocalMachine);

            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
