namespace AutoNetSpy;

public sealed class DecompileOptions
{
    public static readonly string[] DefaultSkipNamePrefixes =
    [
        "System",
        "Microsoft",
        "mscorlib",
        "netstandard",
        "WindowsBase",
        "PresentationCore",
        "PresentationFramework",
        "PresentationUI",
        "ReachFramework",
        "WindowsFormsIntegration",
        "Accessibility",
        "UIAutomation",
        "DirectWriteForwarder",
    ];

    public static string DefaultSkipNamePrefixText => string.Join(Environment.NewLine, DefaultSkipNamePrefixes);

    public string OutputDirectory { get; set; } = string.Empty;
    public bool CreateProject { get; set; } = true;
    public bool RemoveCompilerGenerated { get; set; } = true;
    public bool SkipResources { get; set; }
    public bool SkipAlreadyDecompiled { get; set; } = true;
    public IReadOnlyList<string> SkipNamePrefixes { get; set; } = DefaultSkipNamePrefixes;
    public int MinSizeKb { get; set; }
    public string LanguageVersion { get; set; } = "Latest";
    public int MaxParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount);

    public static IReadOnlyList<string> ParseSkipNamePrefixes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        return value
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
