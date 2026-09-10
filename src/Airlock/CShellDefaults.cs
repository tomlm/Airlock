using System.Runtime.CompilerServices;

namespace Airlock;

/// <summary>Applies the process-wide CShell settings Airlock depends on.</summary>
internal static class CShellDefaults
{
    /// <summary>
    /// Turns off CShell's command echo.
    /// </summary>
    /// <remarks>
    /// It defaults to on, which writes every command line to stdout. For a script that is a
    /// debugging aid; for a CLI whose stdout is the product it is corruption, and it would land in
    /// the middle of an agent's terminal.
    /// <para>
    /// This lives in the executable rather than in Airlock.Core because a module initializer in a
    /// library reaches out and changes global state for whoever references it, which is exactly what
    /// CA2255 warns about. Nothing in Core launches a process during a unit test, so the library
    /// needs no such hook of its own.
    /// </para>
    /// </remarks>
    [ModuleInitializer]
    internal static void Configure() => CShellNet.Globals.Echo = false;
}
