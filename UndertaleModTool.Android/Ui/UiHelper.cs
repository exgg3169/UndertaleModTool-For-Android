using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace UndertaleModTool.Android.Ui;

/// <summary>
/// Small helpers for building UI in code.
/// </summary>
public static class UiHelper
{
    public static int Dp(this Context context, float dp)
        => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, context.Resources!.DisplayMetrics);

    public static LinearLayout VerticalLayout(Context context, int paddingDp = 12)
    {
        LinearLayout layout = new(context) { Orientation = Orientation.Vertical };
        int p = context.Dp(paddingDp);
        layout.SetPadding(p, p, p, p);
        return layout;
    }

    public static TextView Label(Context context, string text, float sizeSp = 14, bool bold = false)
    {
        TextView view = new(context) { Text = text };
        view.SetTextSize(ComplexUnitType.Sp, sizeSp);
        if (bold)
            view.SetTypeface(view.Typeface, TypefaceStyle.Bold);
        return view;
    }

    public static TextView Header(Context context, string text)
    {
        TextView view = Label(context, text, 13, true);
        view.SetPadding(0, context.Dp(14), 0, context.Dp(4));
        view.SetTextColor(Color.ParseColor("#FF7E57C2"));
        return view;
    }

    public static Button Button(Context context, string text, Action onClick)
    {
        Button button = new(context) { Text = text };
        button.SetAllCaps(false);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static EditText MonospaceEditor(Context context, string text, bool editable = true)
    {
        EditText editor = new(context)
        {
            Text = text,
            Gravity = GravityFlags.Top | GravityFlags.Start,
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagNoSuggestions,
        };
        editor.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        editor.SetTextSize(ComplexUnitType.Sp, 12);
        editor.SetHorizontallyScrolling(true);
        editor.Background = null;
        if (!editable)
        {
            editor.KeyListener = null;
            editor.SetTextIsSelectable(true);
        }
        return editor;
    }

    public static void ShowMessage(Context context, string title, string message, Action onClose = null)
    {
        new AlertDialog.Builder(context)
            .SetTitle(title)!
            .SetMessage(message)!
            .SetPositiveButton("OK", (_, _) => onClose?.Invoke())!
            .SetOnCancelListener(new CancelListener(() => onClose?.Invoke()))!
            .Show();
    }

    /// <summary>Shows a long, selectable, scrollable text.</summary>
    public static void ShowLongText(Context context, string title, string text, Action onClose = null)
    {
        ScrollView scroll = new(context);
        HorizontalScrollView hscroll = new(context);
        TextView view = Label(context, text, 12);
        view.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        view.SetTextIsSelectable(true);
        int p = context.Dp(16);
        view.SetPadding(p, p, p, p);
        hscroll.AddView(view);
        scroll.AddView(hscroll);
        new AlertDialog.Builder(context)
            .SetTitle(title)!
            .SetView(scroll)!
            .SetPositiveButton("OK", (_, _) => onClose?.Invoke())!
            .SetNeutralButton("Copy", (_, _) =>
            {
                var clipboard = (global::Android.Content.ClipboardManager)context.GetSystemService(Context.ClipboardService)!;
                clipboard.PrimaryClip = ClipData.NewPlainText(title, text);
                onClose?.Invoke();
            })!
            .SetOnCancelListener(new CancelListener(() => onClose?.Invoke()))!
            .Show();
    }

    public static void Confirm(Context context, string title, string message, Action onYes, Action onNo = null, string yes = "Yes", string no = "No")
    {
        new AlertDialog.Builder(context)
            .SetTitle(title)!
            .SetMessage(message)!
            .SetPositiveButton(yes, (_, _) => onYes())!
            .SetNegativeButton(no, (_, _) => onNo?.Invoke())!
            .SetOnCancelListener(new CancelListener(() => onNo?.Invoke()))!
            .Show();
    }

    public static void PromptText(Context context, string title, string label, string defaultValue, bool multiline,
                                  Action<string> onResult, string ok = "OK", string cancel = "Cancel")
    {
        LinearLayout layout = VerticalLayout(context, 16);
        if (!string.IsNullOrEmpty(label))
            layout.AddView(Label(context, label));
        EditText input = new(context) { Text = defaultValue ?? "" };
        if (multiline)
        {
            input.InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine;
            input.SetMinLines(3);
            input.SetMaxLines(12);
        }
        else
        {
            input.SetSingleLine(true);
        }
        layout.AddView(input);
        new AlertDialog.Builder(context)
            .SetTitle(title)!
            .SetView(layout)!
            .SetPositiveButton(ok ?? "OK", (_, _) => onResult(input.Text))!
            .SetNegativeButton(cancel ?? "Cancel", (_, _) => onResult(null))!
            .SetOnCancelListener(new CancelListener(() => onResult(null)))!
            .Show();
    }

    /// <summary>
    /// Runs work on a background thread while showing a non-cancellable progress dialog.
    /// </summary>
    public static void RunWithProgress<T>(Activity activity, string title, Func<Action<string>, T> work,
                                          Action<T> onSuccess, Action<Exception> onError = null)
    {
        LinearLayout layout = VerticalLayout(activity, 20);
        ProgressBar bar = new(activity, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Indeterminate = true };
        TextView status = Label(activity, "Working...");
        layout.AddView(bar);
        layout.AddView(status);
        AlertDialog dialog = new AlertDialog.Builder(activity).SetTitle(title)!.SetView(layout)!.SetCancelable(false)!.Create()!;
        dialog.Show();

        void Report(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            activity.RunOnUiThread(() => status.Text = message);
        }

        Task.Run(() =>
        {
            try
            {
                T result = work(Report);
                activity.RunOnUiThread(() =>
                {
                    Dismiss(dialog);
                    onSuccess?.Invoke(result);
                });
            }
            catch (Exception e)
            {
                activity.RunOnUiThread(() =>
                {
                    Dismiss(dialog);
                    if (onError is not null)
                        onError(e);
                    else
                        ShowLongText(activity, title + " failed", e.ToString());
                });
            }
        });
    }

    private static void Dismiss(Dialog dialog)
    {
        try
        {
            dialog.Dismiss();
        }
        catch
        {
            // Activity may already be gone.
        }
    }

    /// <summary>
    /// Pads a screen's root view by the system bar / keyboard insets it receives.
    /// </summary>
    /// <remarks>
    /// Apps targeting Android 15+ are drawn edge-to-edge, and "adjustResize" no longer resizes the
    /// window for the keyboard. On older versions the decor view consumes these insets before they
    /// reach the content, so this is a no-op there.
    /// </remarks>
    public static void FitSystemWindows(View root)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return;
        int left = root.PaddingLeft, top = root.PaddingTop, right = root.PaddingRight, bottom = root.PaddingBottom;
        root.SetOnApplyWindowInsetsListener(new InsetsListener(insets =>
        {
            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            var ime = insets.GetInsets(WindowInsets.Type.Ime());
            root.SetPadding(left + bars.Left, top + bars.Top, right + bars.Right, bottom + Math.Max(bars.Bottom, ime.Bottom));
            return WindowInsets.Consumed!;
        }));
        root.RequestApplyInsets();
    }

    public static void Toast(Context context, string text)
        => global::Android.Widget.Toast.MakeText(context, text, ToastLength.Short)!.Show();

    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private readonly Func<WindowInsets, WindowInsets> _apply;

        public InsetsListener(Func<WindowInsets, WindowInsets> apply) => _apply = apply;

        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets) => _apply(insets);
    }

    private sealed class CancelListener : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        private readonly Action _action;

        public CancelListener(Action action) => _action = action;

        public void OnCancel(IDialogInterface dialog) => _action();
    }
}
