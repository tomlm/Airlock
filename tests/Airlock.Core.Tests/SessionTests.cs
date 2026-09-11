using Airlock.Configuration;
using Airlock.Sandbox;
using Airlock.Session;

namespace Airlock.Tests;

/// <summary>
/// Credentials reach the guest through the writable handoff folder, which both sides delete. What
/// must never happen is one landing in the read-only session folder, which stays mapped for the
/// sandbox's whole life, or in the config file on disk.
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
    [InlineData("AIRLOCK")]
    [InlineData("TERM")]
    [InlineData("KEYBOARD_LAYOUT")]
    public void OrdinaryNames_AreNotFlagged(string name) =>
        Assert.False(SecretGuard.LooksLikeSecret(name));

    [Fact]
    public void WritingASecretToTheReadOnlyShare_IsRefused()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_ROOT"] = @"C:\tools\dotnet",
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
            ["DOTNET_ROOT"] = @"C:\tools\dotnet",
            ["AIRLOCK"] = "1",
        };

        SecretGuard.AssertNoSecrets(env);
    }
}

/// <summary>
/// The layout is what keeps the two directions apart: one folder the sandbox may only read, and one
/// it may write, which is the only way it can answer at all.
/// </summary>
public class SandboxLayoutTests
{
    [Fact]
    public void TheGuestsOnlyWayToAnswer_IsTheWritableFolder()
    {
        // wsb exec returns neither output nor an exit code, so every one of these is a file.
        var layout = TempLayout();

        foreach (var path in new[] { layout.ReadyPath, layout.SecretsPath, layout.ProbePath })
        {
            Assert.StartsWith(layout.OutDirectory, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheSetupScriptAndEnv_LiveInTheReadOnlyShare()
    {
        var layout = TempLayout();

        Assert.StartsWith(layout.ShareDirectory, layout.SetupScriptPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(layout.ShareDirectory, layout.EnvFilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delete_RemovesEverythingIncludingAnyHandoffLeftBehind()
    {
        var layout = TempLayout();
        var root = layout.Root;

        File.WriteAllText(layout.SecretsPath, "{}");
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

/// <summary>
/// The credential handoff: written from host environment, never stored, removed by whichever side
/// gets there first.
/// </summary>
public class SecretHandoffTests : IDisposable
{
    private const string Name = "AIRLOCK_TEST_SECRET";

    private readonly SandboxLayout _layout = SandboxLayout.At(
        Path.Combine(Path.GetTempPath(), "airlock-tests", Guid.NewGuid().ToString("N")));

    public SecretHandoffTests() => _layout.Reset();

    [Fact]
    public void OnlyVariablesActuallySetOnTheHost_AreHandedOver()
    {
        Environment.SetEnvironmentVariable(Name, "value-from-host");

        var written = SetupScript.WriteSecrets(_layout, [Name, "AIRLOCK_TEST_MISSING"]);

        Assert.Equal([Name], written);
        Assert.Contains("value-from-host", File.ReadAllText(_layout.SecretsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingSet_WritesNoFileAtAll()
    {
        var written = SetupScript.WriteSecrets(_layout, ["AIRLOCK_TEST_DEFINITELY_MISSING"]);

        Assert.Empty(written);
        Assert.False(File.Exists(_layout.SecretsPath));
    }

    [Fact]
    public void DeleteSecrets_ClearsTheHandoff()
    {
        // The guest deletes this as soon as it has read it; the host deletes it again in case the
        // guest died in between. Either call has to work on its own.
        Environment.SetEnvironmentVariable(Name, "value-from-host");
        SetupScript.WriteSecrets(_layout, [Name]);

        SetupScript.DeleteSecrets(_layout);

        Assert.False(File.Exists(_layout.SecretsPath));
    }

    [Fact]
    public void DeleteSecrets_IsSafeWhenTheGuestAlreadyDidIt()
    {
        SetupScript.DeleteSecrets(_layout);
        SetupScript.DeleteSecrets(_layout);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Name, null);
        _layout.Delete();
        GC.SuppressFinalize(this);
    }
}

/// <summary>The provisioning script is generated from a template, so nothing may be left unfilled.</summary>
public class SetupScriptTests : IDisposable
{
    private readonly SandboxLayout _layout = SandboxLayout.At(
        Path.Combine(Path.GetTempPath(), "airlock-tests", Guid.NewGuid().ToString("N")));

    public SetupScriptTests() => _layout.Reset();

    [Fact]
    public void EveryTokenIsSubstituted()
    {
        SetupScript.Write(_layout, [@"C:\tools\dotnet"], Env(), new NetworkConfig());

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.DoesNotContain("{{", script, StringComparison.Ordinal);
        Assert.Contains(@"C:\tools\dotnet", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuestStillBlocksThePrivateRangesItIsNotPartOf()
    {
        // The fix must not become "stop blocking anything": the broad ranges are still named, and
        // it is only the guest's own subnet that gets carved out of them.
        SetupScript.Write(_layout, [], Env(), new NetworkConfig { BlockLan = true });

        var script = File.ReadAllText(_layout.SetupScriptPath);

        foreach (var range in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16" })
        {
            Assert.Contains(range, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProvisioningRegistersABrowserForHttpLinks()
    {
        // Windows Sandbox ships Edge but never registers it with the shell, so an https:// link
        // opens nothing at all and says nothing about it. The class has to go under HKLM, because
        // this runs as SYSTEM before the desktop account exists to have an HKCU of its own.
        SetupScript.Write(_layout, [], Env(), new NetworkConfig());

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.Contains(@"HKLM:\SOFTWARE\Classes\", script, StringComparison.Ordinal);
        Assert.Contains("msedge.exe", script, StringComparison.Ordinal);
        Assert.Contains("URL Protocol", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvisioningChecksThatNamesResolve()
    {
        // Reported in ready.json, so a firewall change that takes DNS down is something `start`
        // says out loud rather than something discovered an hour later.
        SetupScript.Write(_layout, [], Env(), new NetworkConfig());

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.Contains("Resolve-DnsName", script, StringComparison.Ordinal);
        Assert.Contains("dns = $dns", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SandboxPaths.Out)]
    [InlineData(SandboxPaths.Session)]
    [InlineData(SandboxPaths.Setup)]
    [InlineData(SandboxPaths.Airlocks)]
    public void TheScriptAgreesWithSandboxPaths(string path)
    {
        // setup.ps1 spells these out rather than being told them, so renaming a folder in C# and
        // not in the script would leave provisioning writing its verdict where nobody is looking.
        SetupScript.Write(_layout, [], Env(), new NetworkConfig());

        Assert.Contains(path, File.ReadAllText(_layout.SetupScriptPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAirlocksFolderIsSubstitutedOntoTheDrive()
    {
        // A: is what `open` hands to `wsb exec -d` and what the CLI prints, so if the script stops
        // creating it, every one of those paths becomes a folder that does not exist.
        SetupScript.Write(_layout, [], Env(), new NetworkConfig());

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.Contains("subst", script, StringComparison.Ordinal);
        Assert.Contains(SandboxPaths.Drive, script, StringComparison.Ordinal);
    }

    [Fact]
    public void PathIsRewrittenWithoutExpandingIt()
    {
        // Reading Path expanded bakes in literals and demotes REG_EXPAND_SZ to REG_SZ, which breaks
        // every later variable the guest relies on.
        SetupScript.Write(_layout, [@"C:\tools\git\cmd"], Env(), new NetworkConfig());

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.Contains("DoNotExpandEnvironmentNames", script, StringComparison.Ordinal);
        Assert.Contains("ExpandString", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockingTheLan_SubtractsTheGuestsOwnSubnetRatherThanAllowingItBack()
    {
        // The bug this replaces: the script wrote a broad Block rule over 172.16.0.0/12 and a
        // narrow Allow rule for the guest's own subnet, on the assumption that the more specific
        // allow would win. Windows Firewall evaluates block rules first and an explicit block beats
        // an explicit allow, so the allow sat there enabled and inert - and since the sandbox's DNS
        // server IS its gateway, inside that /12, name resolution died while raw IP traffic kept
        // working. The exemptions have to come out of the blocked set before the rule is written.
        SetupScript.Write(_layout, [], Env(), new NetworkConfig { BlockLan = true });

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.DoesNotContain("-Action Allow", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Subnet", script, StringComparison.Ordinal);

        // The three things that have to stay reachable, or the guest is not a working machine.
        Assert.Contains("Get-NetRoute", script, StringComparison.Ordinal);
        Assert.Contains("Get-DnsClientServerAddress", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetIPAddress", script, StringComparison.Ordinal);
        Assert.Contains("172.16.0.0/12", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowedAddresses_AreQuotedAsPowerShellLiterals()
    {
        SetupScript.Write(
            _layout,
            [],
            Env(),
            new NetworkConfig { BlockLan = true, Allow = ["192.168.1.50"] });

        Assert.Contains("'192.168.1.50'", File.ReadAllText(_layout.SetupScriptPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretInTheMachineEnvironment_IsRefused()
    {
        var env = Env();
        env["ANTHROPIC_API_KEY"] = "sk-nope";

        Assert.Throws<InvalidOperationException>(
            () => SetupScript.Write(_layout, [], env, new NetworkConfig()));
    }

    private static Dictionary<string, string> Env() =>
        new(StringComparer.Ordinal) { ["AIRLOCK"] = "1" };

    public void Dispose()
    {
        _layout.Delete();
        GC.SuppressFinalize(this);
    }
}
