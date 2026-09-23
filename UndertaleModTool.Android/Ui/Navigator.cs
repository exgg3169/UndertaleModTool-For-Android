using System.Collections;
using Android.App;
using Android.Content;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Android.Activities;
using UndertaleModTool.Android.Services;

namespace UndertaleModTool.Android.Ui;

/// <summary>
/// Opens the right screen for an object and produces display names for objects.
/// </summary>
public static class Navigator
{
    public const string ExtraHandle = "handle";
    public const string ExtraTitle = "title";

    public static void Open(Context context, object target, string title = null)
    {
        target = Unwrap(target);
        if (target is null)
        {
            UiHelper.Toast(context, "(null)");
            return;
        }

        DataSession.Selected = target;
        Type activity = target switch
        {
            UndertaleCode => typeof(CodeEditorActivity),
            string => typeof(ObjectEditorActivity),
            IEnumerable => typeof(ResourceListActivity),
            _ => typeof(ObjectEditorActivity),
        };

        Intent intent = new(context, activity);
        intent.PutExtra(ExtraHandle, DataSession.Park(target));
        intent.PutExtra(ExtraTitle, title ?? DisplayName(target));
        if (context is not Activity)
            intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }

    /// <summary>Resolves resource-by-id wrappers to the resource they point to.</summary>
    public static object Unwrap(object obj)
    {
        if (obj is UndertaleResourceRef)
            return obj.GetType().GetProperty("Resource")?.GetValue(obj);
        return obj;
    }

    public static string DisplayName(object obj)
    {
        obj = Unwrap(obj);
        try
        {
            return obj switch
            {
                null => "(null)",
                UndertaleString s => s.Content ?? "(null string)",
                UndertaleNamedResource { Name: not null } named => named.Name.Content ?? "(unnamed)",
                UndertaleVariable v => v.Name?.Content ?? "(variable)",
                string s => s,
                IList list => $"{FriendlyTypeName(obj.GetType())} ({list.Count} items)",
                _ => obj.ToString() ?? obj.GetType().Name,
            };
        }
        catch (Exception e)
        {
            return $"({obj?.GetType().Name}: {e.Message})";
        }
    }

    public static string FriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name.Replace("Undertale", "");
        string name = type.Name[..type.Name.IndexOf('`')];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName)) + ">";
    }
}
