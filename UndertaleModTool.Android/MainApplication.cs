using Android.App;
using Android.Runtime;
using UndertaleModLib.Decompiler;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Core.Scripting;

namespace UndertaleModTool.Android;

[Application(Label = "@string/app_name", Icon = "@drawable/ic_launcher", Theme = "@style/AppTheme", LargeHeap = true)]
public class MainApplication : Application
{
    public MainApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        try
        {
            StorageHelper.Initialize(this);
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Error("UMT", "Failed to extract bundled assets: " + e);
        }

        // UndertaleModLib looks for "GameSpecificData" next to the executable by default.
        GameSpecificResolver.BaseDirectory = StorageHelper.LibDataDirectory;

        // Scripts compile against the reference assemblies embedded into this app (see the .csproj).
        ScriptCompiler.ReferenceAssemblySource = typeof(MainApplication).Assembly;
        ScriptCompiler.DefaultBaseDirectory = StorageHelper.WorkDirectory;
    }
}
