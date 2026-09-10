using System.Runtime.InteropServices;

namespace Airlock.Session;

/// <summary>
/// Restores the console mode that a child process changed.
/// </summary>
/// <remarks>
/// A full-screen TUI turns off echo and line input on the way in and restores them on the way out -
/// unless it is killed, in which case the terminal is left silently swallowing keystrokes. Since
/// Airlock exists to run exactly that kind of program, and to be interrupted, it snapshots the mode
/// and puts it back.
/// </remarks>
public sealed class ConsoleModeGuard : IDisposable
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;

    private readonly IntPtr _stdIn;
    private readonly IntPtr _stdOut;
    private readonly uint? _inMode;
    private readonly uint? _outMode;

    private ConsoleModeGuard(IntPtr stdIn, IntPtr stdOut, uint? inMode, uint? outMode)
    {
        _stdIn = stdIn;
        _stdOut = stdOut;
        _inMode = inMode;
        _outMode = outMode;
    }

    public static ConsoleModeGuard Capture()
    {
        var stdIn = GetStdHandle(StdInputHandle);
        var stdOut = GetStdHandle(StdOutputHandle);

        return new ConsoleModeGuard(
            stdIn,
            stdOut,
            GetConsoleMode(stdIn, out var inMode) ? inMode : null,
            GetConsoleMode(stdOut, out var outMode) ? outMode : null);
    }

    public void Dispose()
    {
        if (_inMode is uint i)
        {
            SetConsoleMode(_stdIn, i);
        }

        if (_outMode is uint o)
        {
            SetConsoleMode(_stdOut, o);
        }
    }

    // Plain DllImport, not LibraryImport: every parameter here is blittable, so the source
    // generator would buy nothing and would force AllowUnsafeBlocks on the whole assembly.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
