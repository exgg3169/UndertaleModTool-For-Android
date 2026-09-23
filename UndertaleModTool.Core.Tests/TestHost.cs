using UndertaleModLib;
using UndertaleModLib.Decompiler;

namespace UndertaleModTool.Core.Tests;

/// <summary>An <see cref="IUmtHost"/> for tests, backed by a data file on disk.</summary>
public sealed class TestHost : IUmtHost
{
    private GlobalDecompileContext _context;

    public UndertaleData Data { get; private set; }
    public string FilePath { get; set; }
    public string DataName => Path.GetFileName(FilePath);
    public bool IsModified { get; private set; }
    public string WorkDirectory { get; } = Path.Combine(Path.GetTempPath(), "umt-core-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public string ScriptsDirectory => Path.Combine(TestContext.RepoRoot, "UndertaleModTool.Android", "Scripts");
    public string ExePath => Path.Combine(TestContext.RepoRoot, "UndertaleModLib");
    public GlobalDecompileContext DecompileContext => _context ??= new GlobalDecompileContext(Data);

    public TestHost() => Directory.CreateDirectory(WorkDirectory);

    public void LoadDataFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        Data = UndertaleIO.Read(fs);
        FilePath = path;
        _context = null;
        IsModified = false;
    }

    public string SaveDataFile(string path)
    {
        path ??= FilePath;
        using (FileStream fs = File.Create(path))
            UndertaleIO.Write(fs, Data);
        IsModified = false;
        return path;
    }

    public void MarkModified(bool codeChanged = false)
    {
        IsModified = true;
        if (codeChanged)
            _context = null;
    }

    public void RunOnMainThread(Action action) => action();
}
