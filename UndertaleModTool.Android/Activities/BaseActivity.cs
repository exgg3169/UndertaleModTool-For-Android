using Android.App;
using Android.Content;
using Android.Views;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// Activity with callback-based activity results and an "up" button.
/// </summary>
public abstract class BaseActivity : Activity
{
    private readonly Dictionary<int, Action<Result, Intent>> _pendingResults = new();
    private int _nextRequestCode = 1000;

    public void StartForResult(Intent intent, Action<Result, Intent> callback)
    {
        int code = _nextRequestCode++;
        _pendingResults[code] = callback;
        StartActivityForResult(intent, code);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (_pendingResults.Remove(requestCode, out var callback))
            callback(resultCode, data);
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        if (item.ItemId == global::Android.Resource.Id.Home)
        {
            OnBackPressed();
            return true;
        }
        return base.OnOptionsItemSelected(item);
    }
}
