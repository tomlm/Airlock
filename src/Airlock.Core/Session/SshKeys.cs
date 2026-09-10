using CShellNet;

namespace Airlock.Session;

/// <summary>Generates the per-session SSH identity.</summary>
/// <remarks>
/// A fresh keypair per session means a key that is worthless the moment the sandbox is destroyed,
/// and nothing to revoke if a session goes wrong.
/// </remarks>
public static class SshKeys
{
    public static string SshExe { get; } = FindOpenSshTool("ssh.exe");

    public static string SshKeygenExe { get; } = FindOpenSshTool("ssh-keygen.exe");

    /// <summary>
    /// Writes an ed25519 keypair with no passphrase into the session's unmapped key directory.
    /// </summary>
    public static async Task GenerateAsync(SessionLayout layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var shell = new CShell { Echo = false };

        // -N "" is the empty passphrase. Passing it as a real empty argument works here because
        // the argument list is handed to the process directly; it is only shells that lose it.
        var result = await shell
            .Run(
                opt => opt.CancellationToken(cancellationToken),
                SshKeygenExe,
                "-t", "ed25519",
                "-N", string.Empty,
                "-C", "airlock-session",
                "-f", layout.PrivateKeyPath,
                "-q")
            .AsResult()
            .ConfigureAwait(false);

        if (!File.Exists(layout.PrivateKeyPath) || !File.Exists(layout.PublicKeyPath))
        {
            throw new InvalidOperationException(
                $"ssh-keygen did not produce a key pair (exit {result.ExitCode}). {result.StandardError}".Trim());
        }

        if (File.ReadAllText(layout.PrivateKeyPath).Contains("ENCRYPTED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ssh-keygen produced a passphrase-protected key; the empty -N did not survive.");
        }

        await LockDownAsync(shell, layout.PrivateKeyPath, cancellationToken).ConfigureAwait(false);

        // The sandbox only ever sees the public half.
        File.Copy(layout.PublicKeyPath, layout.AuthorizedKeyPath, overwrite: true);
    }

    /// <summary>
    /// ssh.exe refuses a private key whose ACL is loose ("UNPROTECTED PRIVATE KEY FILE"), so the
    /// inherited permissions have to go.
    /// </summary>
    private static async Task LockDownAsync(CShell shell, string keyPath, CancellationToken cancellationToken)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";

        await shell
            .Run(
                opt => opt.CancellationToken(cancellationToken),
                "icacls.exe", keyPath, "/inheritance:r", "/grant", $"{user}:F")
            .AsResult()
            .ConfigureAwait(false);
    }

    private static string FindOpenSshTool(string fileName)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

        var candidates = new[]
        {
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH", fileName),
            System.IO.Path.Combine(system32, "OpenSSH", fileName),
        };

        return candidates.FirstOrDefault(File.Exists) ?? fileName;
    }
}
