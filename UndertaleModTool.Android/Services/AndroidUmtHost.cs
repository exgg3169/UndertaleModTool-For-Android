using Android.Content;
using Android.OS;
using UndertaleModLib;
using UndertaleModLib.Decompiler;
using UndertaleModTool.Core;

namespace UndertaleModTool.Android.Services;

/// <summary>
/// Connects the platform-independent tools (MCP server) to the app's session.
/// </summary>
public sealed class AndroidUmtHost : IUmtHost
{
    private readonly Context _context;
    private readonly Handler _mainHandler = new(Looper.MainLooper!);

    public AndroidUmtHost(Context context) => _context = context.ApplicationContext;

    public UndertaleData Data => DataSession.Data;
    public string DataName => DataSession.DisplayName;
    public bool IsModified => DataSession.IsModified;
    public string WorkDirectory => StorageHelper.WorkDirectory;
    public string ScriptsDirectory => StorageHelper.ScriptsDirectory;
    public string ExePath => StorageHelper.LibDataDirectory;
    public GlobalDecompileContext DecompileContext => DataSession.DecompileContext;

    public void LoadDataFile(string path)
    {
        UndertaleData data = DataSession.LoadFromFile(path, null);
        DataSession.SetData(data, path, null, Path.GetFileName(path));
    }

    public string SaveDataFile(string path)
    {
        if (path is not null)
        {
            DataSession.SaveToFile(path, null);
            DataSession.IsModified = false;
            return path;
        }
        if (DataSession.SourceUri is not null)
        {
            DataSession.SaveToUri(_context, DataSession.SourceUri, null);
            return DataSession.DisplayName;
        }
        if (DataSession.FilePath is not null)
        {
            DataSession.SaveToFile(DataSession.FilePath, null);
            DataSession.IsModified = false;
            return DataSession.FilePath;
        }
        throw new InvalidOperationException("This data file has no location yet; give a path.");
    }

    public void MarkModified(bool codeChanged = false)
    {
        DataSession.IsModified = true;
        if (codeChanged)
            DataSession.InvalidateDecompileContext();
    }

    public void RunOnMainThread(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper)
        {
            action();
            return;
        }
        Exception error = null;
        using ManualResetEventSlim done = new();
        _mainHandler.Post(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
