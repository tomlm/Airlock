namespace Airlock.Sandbox;

/// <summary>A <c>wsb.exe</c> invocation failed. Carries everything needed to explain why.</summary>
public sealed class WsbException : Exception
{
    public WsbException(string message, string arguments, int exitCode, string stdout, string stderr)
        : base(message)
    {
        Arguments = arguments;
        ExitCode = exitCode;
        StandardOutput = stdout;
        StandardError = stderr;
    }

    public string Arguments { get; }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    /// <summary>
    /// A stale Windows Sandbox base image surfaces as <c>0x80070002</c>. The fix needs elevation
    /// (stop CmService, rename <c>C:\ProgramData\Microsoft\Windows\Containers</c>, start it again),
    /// so Airlock prints the commands rather than attempting them.
    /// </summary>
    public bool IsStaleBaseImage =>
        StandardError.Contains("0x80070002", StringComparison.OrdinalIgnoreCase) ||
        StandardOutput.Contains("0x80070002", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows Sandbox is single-instance: a second <c>wsb start</c> fails with
    /// <c>0x800401F6 CO_E_APPSINGLEUSE</c> even when the running sandbox is not Airlock's.
    /// </summary>
    public bool IsAlreadyRunning =>
        StandardError.Contains("0x800401F6", StringComparison.OrdinalIgnoreCase) ||
        StandardError.Contains("more than once", StringComparison.OrdinalIgnoreCase) ||
        StandardOutput.Contains("0x800401F6", StringComparison.OrdinalIgnoreCase) ||
        StandardOutput.Contains("more than once", StringComparison.OrdinalIgnoreCase);
}
