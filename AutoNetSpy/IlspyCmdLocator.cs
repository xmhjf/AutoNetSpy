using System.Diagnostics;
using System.IO;

namespace AutoNetSpy;

public static class IlspyCmdLocator
{
    public static string? Find()
    {
        var path = ResolveFromPath("ilspycmd.exe") ?? ResolveFromPath("ilspycmd");
        if (path != null) return path;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(userProfile, ".dotnet", "tools", "ilspycmd.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public static async Task<(bool ok, string log)> InstallAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("dotnet", "tool install -g ilspycmd")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode == 0, stdout + stderr);
    }

    private static string? ResolveFromPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
