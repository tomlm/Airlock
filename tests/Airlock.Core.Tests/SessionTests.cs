using Airlock.Session;

namespace Airlock.Tests;

/// <summary>
/// Secrets are supposed to reach the sandbox over the SSH channel and never through a file. The
/// session's share folder is mapped into the sandbox, so anything written there is readable by the
/// agent - which makes this the difference between forwarding a credential and handing it over.
/// </summary>
public class SecretGuardTests
{
    [Theory]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("DB_PASSWORD")]
    [InlineData("SOME_CREDENTIAL")]
    [InlineData("MY_PAT")]
    [InlineData("anthropic_api_key")]
    public void SecretLookingNames_AreRecognised(string name) =>
        Assert.True(SecretGuard.LooksLikeSecret(name));

    [Theory]
    [InlineData("DOTNET_ROOT")]
    [InlineData("PATH")]
    [InlineData("AIRLOCK_PROJECT")]
    [InlineData("TERM")]
    [InlineData("KEYBOARD_LAYOUT")]
    public void OrdinaryNames_AreNotFlagged(string name) =>
        Assert.False(SecretGuard.LooksLikeSecret(name));

    [Fact]
    public void WritingASecretToTheMappedShare_IsRefused()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = @"C:\airlock\dotnet",
            ["ANTHROPIC_API_KEY"] = "sk-should-never-be-written",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => SecretGuard.AssertNoSecrets(env));

        Assert.Contains("ANTHROPIC_API_KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonSecretEnvironment_IsAccepted()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = @"C:\airlock\dotnet",
            ["AIRLOCK"] = "1",
        };

        SecretGuard.AssertNoSecrets(env);
    }
}

/// <summary>
/// The remote command crosses three layers of quoting on its way to the guest shell, so it is sent
/// base64-encoded. These cover the encoding and the PowerShell literal quoting underneath it.
/// </summary>
public class RemoteCommandTests
{
    [Fact]
    public void Encode_ProducesBase64OfUtf16()
    {
        // PowerShell's -EncodedCommand requires UTF-16LE, not UTF-8.
        var encoded = SshLauncher.Encode("echo hi");
        var decoded = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Equal("echo hi", decoded);
    }

    [Fact]
    public void RemoteCommand_MovesToTheProjectFirst()
    {
        var script = SshLauncher.BuildRemoteCommand(@"C:\work\Foo", ["claude"]);

        Assert.Contains(@"Set-Location -LiteralPath 'C:\work\Foo'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteCommand_PassesArgumentsThrough()
    {
        var script = SshLauncher.BuildRemoteCommand(@"C:\work\Foo", ["claude", "--resume"]);

        Assert.Contains("& 'claude' @('--resume')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteCommand_PropagatesTheExitCode()
    {
        // The wrapped command's exit code is what airlock itself returns, so it has to survive.
        var script = SshLauncher.BuildRemoteCommand(@"C:\work\Foo", ["dotnet", "test"]);

        Assert.Contains("exit $LASTEXITCODE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleQuotesInArguments_AreEscapedNotInjected()
    {
        var script = SshLauncher.BuildRemoteCommand(@"C:\work\Foo", ["claude", "it's"]);

        // Doubling is how a PowerShell single-quoted literal escapes a quote.
        Assert.Contains("'it''s'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PathsWithSpaces_StaySingleArguments()
    {
        var script = SshLauncher.BuildRemoteCommand(@"C:\work\My Project", ["claude"]);

        Assert.Contains(@"'C:\work\My Project'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommand_YieldsOnlyThePrologue()
    {
        var script = SshLauncher.BuildRemoteCommand(@"C:\work\Foo", []);

        Assert.DoesNotContain("exit $LASTEXITCODE", script, StringComparison.Ordinal);
    }
}

/// <summary>The private key must never end up somewhere the sandbox can read.</summary>
public class SandboxLayoutTests
{
    [Fact]
    public void PrivateKey_IsNotInsideTheMappedShare()
    {
        var layout = TempLayout();

        Assert.StartsWith(layout.KeyDirectory, layout.PrivateKeyPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(layout.ShareDirectory, layout.PrivateKeyPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyThePublicKey_IsPlacedInTheShare()
    {
        var layout = TempLayout();

        Assert.StartsWith(layout.ShareDirectory, layout.AuthorizedKeyPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".pub", layout.AuthorizedKeyPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_RemovesTheSessionDirectoryAndTheKeyWithIt()
    {
        // The sandbox now outlives a single command, so this is what `airlock stop` relies on to
        // finally take the private key off disk.
        var layout = TempLayout();
        var root = layout.Root;

        File.WriteAllText(layout.PrivateKeyPath, "PRIVATE KEY");
        Assert.True(Directory.Exists(root));

        layout.Delete();

        Assert.False(Directory.Exists(root));
    }

    private static SandboxLayout TempLayout()
    {
        var layout = SandboxLayout.At(
            Path.Combine(Path.GetTempPath(), "airlock-tests", Guid.NewGuid().ToString("N")));

        layout.Reset();

        return layout;
    }
}
