namespace Airlock.Session;

/// <summary>
/// The host-side folders backing one session, and which of them the sandbox can see.
/// </summary>
/// <remarks>
/// The split is the point. <see cref="ShareDirectory"/> is mapped into the sandbox read-only, so
/// everything in it is readable by the agent. <see cref="KeyDirectory"/> holds the session's
/// <b>private</b> key and is deliberately not mapped anywhere.
/// </remarks>
public sealed class SessionLayout : IDisposable
{
    private SessionLayout(string id, string root)
    {
        Id = id;
        Root = root;
    }

    /// <summary>Short, filesystem-safe, and used in log lines the user reads.</summary>
    public string Id { get; }

    public string Root { get; }

    /// <summary>Mapped read-only to <c>C:\airlock\session</c>: setup script, public key, env.</summary>
    public string ShareDirectory => System.IO.Path.Combine(Root, "share");

    /// <summary>Never mapped. The private key would otherwise be readable by the agent.</summary>
    public string KeyDirectory => System.IO.Path.Combine(Root, "key");

    /// <summary>Attached read-write only when provisioning fails, to retrieve the log.</summary>
    public string OutDirectory => System.IO.Path.Combine(Root, "out");

    public string PrivateKeyPath => System.IO.Path.Combine(KeyDirectory, "id_ed25519");

    public string PublicKeyPath => PrivateKeyPath + ".pub";

    public string SetupScriptPath => System.IO.Path.Combine(ShareDirectory, "setup.ps1");

    public string AuthorizedKeyPath => System.IO.Path.Combine(ShareDirectory, "authorized_key.pub");

    public string EnvFilePath => System.IO.Path.Combine(ShareDirectory, "env.json");

    /// <summary>Written for diagnostics only; the sandbox is launched from inline XML.</summary>
    public string ConfigPath => System.IO.Path.Combine(Root, "session.wsb");

    public static SessionLayout Create(string sandboxId)
    {
        // A short id keeps folder names readable; the full sandbox GUID is what wsb tracks.
        var shortId = sandboxId.Replace("-", string.Empty, StringComparison.Ordinal)[..8];
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Airlock", shortId);

        var layout = new SessionLayout(shortId, root);

        Directory.CreateDirectory(layout.ShareDirectory);
        Directory.CreateDirectory(layout.KeyDirectory);
        Directory.CreateDirectory(layout.OutDirectory);

        return layout;
    }

    /// <summary>
    /// Removes the whole session directory. This is what gets the private key off disk, so it runs
    /// even when the session ended badly.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file left open by a dying ssh.exe should not mask the real failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
