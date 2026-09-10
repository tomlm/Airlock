using Airlock.Configuration;
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
            ["DOTNET_ROOT"] = @"C:\airlock\_tools_\dotnet",
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
            ["DOTNET_ROOT"] = @"C:\airlock\_tools_\dotnet",
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
        SetupScript.Write(_layout, [@"C:\airlock\_tools_\dotnet"], Env(), new NetworkConfig());

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.DoesNotContain("{{", script, StringComparison.Ordinal);
        Assert.Contains(@"C:\airlock\_tools_\dotnet", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PathIsRewrittenWithoutExpandingIt()
    {
        // Reading Path expanded bakes in literals and demotes REG_EXPAND_SZ to REG_SZ, which breaks
        // every later variable the guest relies on.
        SetupScript.Write(_layout, [@"C:\airlock\_tools_\git\cmd"], Env(), new NetworkConfig());

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.Contains("DoNotExpandEnvironmentNames", script, StringComparison.Ordinal);
        Assert.Contains("ExpandString", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockingTheLan_AllowsTheGuestsOwnSubnetFirst()
    {
        // The guest's own address and gateway sit inside 172.16.0.0/12, so a blanket block would
        // cut its DNS and default route.
        SetupScript.Write(_layout, [], Env(), new NetworkConfig { BlockLan = true });

        var script = File.ReadAllText(_layout.SetupScriptPath);

        Assert.Contains("airlock-allow-own", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetRoute", script, StringComparison.Ordinal);
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
