namespace UndertaleModTool.Core.Tests;

/// <summary>Minimal test runner: prints OK/FAIL per check; the exit code is the number of failures.</summary>
public sealed class TestContext
{
    public int Failures { get; private set; }

    public void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "OK    " : "FAIL  ") + what);
        if (!ok)
            Failures++;
    }

    /// <summary>The repository root (found by walking up to the solution file).</summary>
    public static string RepoRoot
    {
        get
        {
            DirectoryInfo dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UndertaleModTool.Android.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}

public static class Program
{
    public static async Task<int> Main()
    {
        TestContext t = new();
        Console.WriteLine("== Imaging ==");
        ImagingTests.Run(t);
        Console.WriteLine("== MCP server ==");
        await McpTests.Run(t);
        Console.WriteLine(t.Failures == 0 ? "ALL PASSED" : $"{t.Failures} FAILED");
        return t.Failures;
    }
}
