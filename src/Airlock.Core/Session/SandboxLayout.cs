namespace Airlock.Session;

/// <summary>
/// The host-side folders backing the running sandbox, and which of them it can see.
/// </summary>
/// <remarks>
/// <para>
/// The split is the point. <see cref="ShareDirectory"/> is mapped into the sandbox read-only, so
/// everything in it is readable by the agent. <see cref="KeyDirectory"/> holds the sandbox's
/// <b>private</b> key and is deliberately not mapped anywhere.
/// </para>
/// <para>
/// These live under LocalApplicationData rather than TEMP because the sandbox outlives the command
/// that started it: a later <c>airlock</c> in another folder needs the same key to connect. They
/// are removed by <c>airlock stop</c>, which is what finally takes the private key off disk.
/// </para>
/// </remarks>
public sealed class SandboxLayout
{
    private SandboxLayout(string root) => Root = root;

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
    public string ConfigPath => System.IO.Path.Combine(Root, "sandbox.wsb");

    public static SandboxLayout Default { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Airlock",
        "session"));

    public static SandboxLayout At(string root) => new(root);

    /// <summary>Starts from clean directories, so a stale key can never outlive its sandbox.</summary>
    public void Reset()
    {
        Delete();

        Directory.CreateDirectory(ShareDirectory);
        Directory.CreateDirectory(KeyDirectory);
        Directory.CreateDirectory(OutDirectory);
    }

    public void Delete()
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
            // A file still held by a dying ssh.exe must not mask the real failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
