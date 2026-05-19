using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoNetSpy;

public sealed record DecompileResult(
    string Assembly,
    string OutputPath,
    bool Success,
    string? ErrorLog,
    TimeSpan Duration,
    bool Skipped = false);

public sealed class DecompileService
{
    private static readonly Regex CompilerGeneratedFileRegex = new(
        @"(^<>|^<PrivateImplementationDetails>|^<Module>|\$\$)",
        RegexOptions.Compiled);

    private readonly string _ilspyCmdPath;
    private readonly DecompileOptions _options;
    private readonly string[] _skipNamePrefixes;

    public DecompileService(string ilspyCmdPath, DecompileOptions options)
    {
        _ilspyCmdPath = ilspyCmdPath;
        _options = options;
        _skipNamePrefixes = options.SkipNamePrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<DecompileResult>> RunAsync(
        IReadOnlyList<AssemblyNode> assemblies,
        IProgress<(int done, int total, string current, bool skipped)>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_options.OutputDirectory);
        var logDir = Path.Combine(_options.OutputDirectory, "_logs");
        Directory.CreateDirectory(logDir);

        var filtered = Filter(assemblies);
        var workItems = CreateWorkItems(filtered);

        var results = new ConcurrentBag<DecompileResult>();
        int done = 0;

        await Parallel.ForEachAsync(
            workItems,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _options.MaxParallelism },
            async (item, token) =>
            {
                var asm = item.Assembly;
                var outDir = item.OutputDirectory;

                if (_options.SkipAlreadyDecompiled && HasDecompiledContent(outDir))
                {
                    results.Add(new DecompileResult(asm.FullPath, outDir, true, null, TimeSpan.Zero, Skipped: true));
                    var ds = Interlocked.Increment(ref done);
                    progress?.Report((ds, workItems.Count, asm.FullPath, true));
                    return;
                }

                Directory.CreateDirectory(outDir);

                var sw = Stopwatch.StartNew();
                var (success, log) = await RunIlspyAsync(asm.FullPath, outDir, token);
                sw.Stop();

                string? logPath = null;
                if (!success || !string.IsNullOrWhiteSpace(log))
                {
                    logPath = Path.Combine(logDir, item.UniqueName + (success ? ".log" : ".err.log"));
                    await File.WriteAllTextAsync(logPath, log, token);
                }

                if (success && _options.RemoveCompilerGenerated)
                    PostProcess(outDir);

                results.Add(new DecompileResult(asm.FullPath, outDir, success, success ? null : logPath, sw.Elapsed));
                var d = Interlocked.Increment(ref done);
                progress?.Report((d, workItems.Count, asm.FullPath, false));
            });

        var summary = new
        {
            Total = results.Count,
            Succeeded = results.Count(r => r.Success && !r.Skipped),
            Skipped = results.Count(r => r.Skipped),
            Failed = results.Count(r => !r.Success),
            Items = results.Select(r => new
            {
                r.Assembly,
                r.OutputPath,
                r.Success,
                r.Skipped,
                r.ErrorLog,
                DurationMs = (long)r.Duration.TotalMilliseconds,
            }).ToArray(),
        };
        await File.WriteAllTextAsync(
            Path.Combine(_options.OutputDirectory, "_summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return results.ToArray();
    }

    private List<AssemblyNode> Filter(IEnumerable<AssemblyNode> assemblies)
    {
        var list = new List<AssemblyNode>();
        foreach (var a in assemblies)
        {
            if (_options.MinSizeKb > 0 && a.SizeBytes < _options.MinSizeKb * 1024L) continue;
            var name = Path.GetFileNameWithoutExtension(a.FullPath);
            if (_skipNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            list.Add(a);
        }
        return list;
    }

    private List<WorkItem> CreateWorkItems(IReadOnlyList<AssemblyNode> assemblies)
    {
        var usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<WorkItem>(assemblies.Count);

        foreach (var assembly in assemblies)
        {
            var name = Path.GetFileNameWithoutExtension(assembly.FullPath);
            var unique = MakeUniqueDirName(usedNames, name);
            items.Add(new WorkItem(assembly, unique, Path.Combine(_options.OutputDirectory, unique)));
        }

        return items;
    }

    private static bool HasDecompiledContent(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        // Consider decompiled if there's at least one .cs or .csproj file
        return Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Any();
    }

    private async Task<(bool ok, string log)> RunIlspyAsync(string assemblyPath, string outDir, CancellationToken ct)
    {
        var args = new List<string> { "-o", outDir };
        if (_options.CreateProject) args.Add("-p");
        if (!string.IsNullOrWhiteSpace(_options.LanguageVersion) &&
            !_options.LanguageVersion.Equals("Latest", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-lv");
            args.Add(_options.LanguageVersion);
        }
        args.Add(assemblyPath);

        var psi = new ProcessStartInfo(_ilspyCmdPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = outDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync(ct);
            var se = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var sb = new StringBuilder();
            sb.AppendLine("$ ilspycmd " + string.Join(" ", args.Select(Quote)));
            sb.Append(await so);
            var err = await se;
            if (!string.IsNullOrEmpty(err)) { sb.AppendLine("--- STDERR ---"); sb.Append(err); }
            return (p.ExitCode == 0, sb.ToString());
        }
        catch (Exception ex)
        {
            return (false, ex.ToString());
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    private static string MakeUniqueDirName(Dictionary<string, int> used, string baseName)
    {
        var n = used.TryGetValue(baseName, out var value) ? value + 1 : 1;
        used[baseName] = n;
        return n == 1 ? baseName : $"{baseName}_{n}";
    }

    private sealed record WorkItem(AssemblyNode Assembly, string UniqueName, string OutputDirectory);

    private void PostProcess(string outDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(outDir, "*.cs", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (CompilerGeneratedFileRegex.IsMatch(name))
                {
                    TryDelete(file);
                }
            }

            if (_options.SkipResources)
            {
                var resDir = Path.Combine(outDir, "Resources");
                if (Directory.Exists(resDir))
                    Directory.Delete(resDir, recursive: true);
            }

            foreach (var dir in Directory.EnumerateDirectories(outDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) &&
                        !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
