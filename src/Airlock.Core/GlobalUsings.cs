// Airlock shells out constantly - wsb.exe, ssh.exe, ssh-keygen.exe, icacls.exe - so CShell's global
// form reads better than threading a shell instance through every class. The namespace itself is
// needed too: AsResult and friends are extension methods on Medallion.Shell.Command.
global using CShellNet;
global using static CShellNet.Globals;
