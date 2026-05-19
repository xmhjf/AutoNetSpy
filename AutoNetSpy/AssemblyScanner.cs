using System.Collections.Concurrent;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;

namespace AutoNetSpy;

public sealed record AssemblyNode(string FullPath, long SizeBytes, string TargetFramework);

public enum ScanPhase { Enumerating, Inspecting }

public sealed record ScanProgress(ScanPhase Phase, int Total, int Done, int FoundAssemblies);

public sealed record DirNode(string Path, string Name)
{
    public List<DirNode> Subdirs { get; } = new();
    public List<AssemblyNode> Assemblies { get; } = new();

    public bool IsEmpty => Subdirs.Count == 0 && Assemblies.Count == 0;
}

public static class AssemblyScanner
{
    private static readonly string[] Extensions = new[] { ".dll", ".exe" };

    public static DirNode Scan(string root, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var rootInfo = new DirectoryInfo(root);
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        var candidates = new List<string>(1024);
        foreach (var path in Directory.EnumerateFiles(root, "*", enumOpts))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(path);
            if (ext.Length == 4 &&
                (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(path);
                if (candidates.Count % 200 == 0)
                    progress?.Report(new ScanProgress(ScanPhase.Enumerating, candidates.Count, 0, 0));
            }
        }

        progress?.Report(new ScanProgress(ScanPhase.Enumerating, candidates.Count, 0, 0));

        var results = new System.Collections.Concurrent.ConcurrentBag<AssemblyNode>();
        int inspected = 0;
        int total = candidates.Count;
        int parallelism = Math.Max(2, Environment.ProcessorCount);

        Parallel.ForEach(
            candidates,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism },
            file =>
            {
                if (TryInspect(file, out var info))
                    results.Add(info!);
                int n = Interlocked.Increment(ref inspected);
                if (n == total || (n & 31) == 0)
                    progress?.Report(new ScanProgress(ScanPhase.Inspecting, total, n, results.Count));
            });

        progress?.Report(new ScanProgress(ScanPhase.Inspecting, total, total, results.Count));

        return BuildTree(rootInfo, results);
    }

    private static DirNode BuildTree(DirectoryInfo rootInfo, IEnumerable<AssemblyNode> assemblies)
    {
        var rootPath = NormalizeDir(rootInfo.FullName);
        var root = new DirNode(rootPath, rootInfo.Name);
        var dirMap = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase) { [rootPath] = root };

        foreach (var asm in assemblies.OrderBy(a => a.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var dirPath = NormalizeDir(Path.GetDirectoryName(asm.FullPath)!);
            var dirNode = GetOrCreateDir(dirMap, rootPath, dirPath);
            dirNode.Assemblies.Add(asm);
        }

        SortRecursive(root);
        return root;
    }

    private static DirNode GetOrCreateDir(Dictionary<string, DirNode> map, string rootPath, string dirPath)
    {
        if (map.TryGetValue(dirPath, out var existing)) return existing;

        var parentPath = NormalizeDir(Path.GetDirectoryName(dirPath)!);
        var parent = string.Equals(dirPath, rootPath, StringComparison.OrdinalIgnoreCase)
            ? map[rootPath]
            : GetOrCreateDir(map, rootPath, parentPath);

        var node = new DirNode(dirPath, Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        parent.Subdirs.Add(node);
        map[dirPath] = node;
        return node;
    }

    private static string NormalizeDir(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void SortRecursive(DirNode node)
    {
        node.Subdirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        node.Assemblies.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
        foreach (var sub in node.Subdirs) SortRecursive(sub);
    }

    private static bool TryInspect(string path, out AssemblyNode? node)
    {
        node = null;
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return false;

            var tfm = ReadTargetFramework(md) ?? string.Empty;
            node = new AssemblyNode(path, fs.Length, tfm);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadTargetFramework(MetadataReader md)
    {
        foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(handle);
            string? name = GetAttributeTypeName(md, attr);
            if (name != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;

            try
            {
                var value = attr.DecodeValue(DummyAttributeTypeProvider.Instance);
                if (value.FixedArguments.Length > 0 && value.FixedArguments[0].Value is string s)
                    return s;
            }
            catch { }
        }
        return null;
    }

    private static string? GetAttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        try
        {
            switch (attr.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                    var mref = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                    if (mref.Parent.Kind == HandleKind.TypeReference)
                    {
                        var tref = md.GetTypeReference((TypeReferenceHandle)mref.Parent);
                        return $"{md.GetString(tref.Namespace)}.{md.GetString(tref.Name)}";
                    }
                    break;
                case HandleKind.MethodDefinition:
                    var mdef = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                    var tdef = md.GetTypeDefinition(mdef.GetDeclaringType());
                    return $"{md.GetString(tdef.Namespace)}.{md.GetString(tdef.Name)}";
            }
        }
        catch { }
        return null;
    }

    private sealed class DummyAttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly DummyAttributeTypeProvider Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => string.Empty;
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => string.Empty;
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
