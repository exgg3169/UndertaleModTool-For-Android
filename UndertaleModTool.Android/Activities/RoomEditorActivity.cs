using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Android.Services;
using UndertaleModTool.Android.Ui;
using static UndertaleModLib.Models.UndertaleRoom;

namespace UndertaleModTool.Android.Activities;

/// <summary>
/// Visual room editor: view the room, select, move, add, duplicate and delete instances.
/// </summary>
[Activity(Label = "Room editor", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden)]
public class RoomEditorActivity : BaseActivity
{
    private UndertaleRoom _room;
    private RoomView _view;
    private TextView _info;
    private LinearLayout _actions;

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _room = DataSession.Fetch(Intent!.GetIntExtra(Navigator.ExtraHandle, 0)) as UndertaleRoom;
        if (_room is null || DataSession.Data is null)
        {
            Finish();
            return;
        }
        Title = _room.Name?.Content ?? "Room";
        ActionBar?.SetDisplayHomeAsUpEnabled(true);
        ActionBar!.Subtitle = $"{_room.Width} x {_room.Height} - pinch to zoom, drag to pan";

        LinearLayout root = UiHelper.VerticalLayout(this, 0);
        _view = new RoomView(this, _room);
        _view.SelectionChanged += UpdateSelectionInfo;
        root.AddView(_view, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        LinearLayout bottom = UiHelper.VerticalLayout(this, 8);
        _info = UiHelper.Label(this, "", 13);
        bottom.AddView(_info);
        _actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        AddAction("Properties", () => Navigator.Open(this, _view.SelectedItem, ItemName(_view.SelectedItem)));
        AddAction("Duplicate", Duplicate);
        AddAction("Delete", Delete);
        bottom.AddView(_actions);
        root.AddView(bottom);

        SetContentView(root);
        UiHelper.FitSystemWindows(root);
        UpdateSelectionInfo();
    }

    private void AddAction(string text, Action action)
        => _actions.AddView(UiHelper.Button(this, text, action), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));

    protected override void OnResume()
    {
        base.OnResume();
        // Properties may have been edited in another screen.
        _view?.Invalidate();
        UpdateSelectionInfo();
    }

    private static string InstanceName(GameObject obj)
        => obj is null ? "" : $"{obj.ObjectDefinition?.Name?.Content ?? "(no object)"} #{obj.InstanceID}";

    private static string ItemName(object item) => item switch
    {
        GameObject obj => InstanceName(obj),
        Tile tile => $"Tile #{tile.InstanceID} ({tile.ObjectDefinition?.Name?.Content})",
        SpriteInstance sprite => $"{sprite.Name?.Content} ({sprite.Sprite?.Name?.Content})",
        _ => "",
    };

    private void UpdateSelectionInfo()
    {
        object item = _view.SelectedItem;
        _actions.Visibility = item is null ? ViewStates.Gone : ViewStates.Visible;
        string mode = _view.Mode switch
        {
            RoomView.EditMode.Tiles => "Mode: tiles & sprites. Tap one to select it.",
            RoomView.EditMode.Paint => $"Mode: painting tiles on \"{_view.PaintLayer?.LayerName?.Content}\" with " +
                                       (_view.PaintTile == 0 ? "the eraser" : $"tile {_view.PaintTile & 0x7FFFF}") +
                                       ". Drag with one finger to paint, two fingers to move/zoom.",
            _ => $"Mode: instances. Tap one to select it. {_room.GameObjects.Count} instances" +
                 (_room.Layers is { Count: > 0 } ? $", {_room.Layers.Count} layers." : "."),
        };
        _info.Text = item switch
        {
            GameObject obj => $"{InstanceName(obj)}  at ({obj.X}, {obj.Y})  scale {obj.ScaleX}x{obj.ScaleY}  rot {obj.Rotation}°\nDrag it to move it.",
            Tile tile => $"{ItemName(tile)}  at ({tile.X}, {tile.Y})  {tile.Width}x{tile.Height}  depth {tile.TileDepth}\nDrag it to move it.",
            SpriteInstance sprite => $"{ItemName(sprite)}  at ({sprite.X}, {sprite.Y})\nDrag it to move it.",
            _ => mode,
        };
    }

    #region Instance operations

    private Layer LayerOf(GameObject obj)
        => _room.Layers?.FirstOrDefault(l => l.InstancesData?.Instances.Contains(obj) == true);

    private GameObject AddInstance(GameObject obj, Layer layer)
    {
        obj.InstanceID = DataSession.Data.GeneralInfo.LastObj++;
        _room.GameObjects.Add(obj);
        layer?.InstancesData.Instances.Add(obj);
        DataSession.IsModified = true;
        _view.Select(obj);
        return obj;
    }

    private void Duplicate()
    {
        if (_view.SelectedItem is Tile tile)
        {
            DuplicateTile(tile);
            return;
        }
        if (_view.SelectedItem is SpriteInstance)
        {
            UiHelper.Toast(this, "Duplicating asset sprites isn't supported; use Properties.");
            return;
        }
        GameObject src = _view.SelectedInstance;
        if (src is null)
            return;
        int dx = _room.GridWidth >= 1 ? (int)_room.GridWidth : 16;
        int dy = _room.GridHeight >= 1 ? (int)_room.GridHeight : 16;
        AddInstance(new GameObject
        {
            X = src.X + dx,
            Y = src.Y + dy,
            ObjectDefinition = src.ObjectDefinition,
            CreationCode = src.CreationCode,
            ScaleX = src.ScaleX,
            ScaleY = src.ScaleY,
            Color = src.Color,
            Rotation = src.Rotation,
            PreCreateCode = src.PreCreateCode,
            ImageSpeed = src.ImageSpeed,
            ImageIndex = src.ImageIndex,
        }, LayerOf(src));
    }

    private IList<Tile> TileListOf(Tile tile)
    {
        if (_room.Tiles.Contains(tile))
            return _room.Tiles;
        return _room.Layers?.Select(l => l.AssetsData?.LegacyTiles).FirstOrDefault(list => list?.Contains(tile) == true);
    }

    private void DuplicateTile(Tile src)
    {
        IList<Tile> list = TileListOf(src);
        if (list is null)
            return;
        Tile copy = new()
        {
            spriteMode = src.spriteMode,
            X = src.X + (int)src.Width,
            Y = src.Y,
            SourceX = src.SourceX,
            SourceY = src.SourceY,
            Width = src.Width,
            Height = src.Height,
            TileDepth = src.TileDepth,
            ScaleX = src.ScaleX,
            ScaleY = src.ScaleY,
            Color = src.Color,
            InstanceID = DataSession.Data.GeneralInfo.LastTile++,
        };
        if (src.spriteMode)
            copy.SpriteDefinition = src.SpriteDefinition;
        else
            copy.BackgroundDefinition = src.BackgroundDefinition;
        list.Add(copy);
        DataSession.IsModified = true;
        _view.Select(copy);
    }

    private void Delete()
    {
        switch (_view.SelectedItem)
        {
            case Tile tile:
                UiHelper.Confirm(this, "Delete tile", $"Delete {ItemName(tile)}?", () =>
                {
                    TileListOf(tile)?.Remove(tile);
                    DataSession.IsModified = true;
                    _view.Select(null);
                });
                return;
            case SpriteInstance sprite:
                UiHelper.Confirm(this, "Delete sprite", $"Delete {ItemName(sprite)}?", () =>
                {
                    foreach (Layer layer in _room.Layers)
                        layer.AssetsData?.Sprites?.Remove(sprite);
                    DataSession.IsModified = true;
                    _view.Select(null);
                });
                return;
        }
        GameObject obj = _view.SelectedInstance;
        if (obj is null)
            return;
        UiHelper.Confirm(this, "Delete instance", $"Delete {InstanceName(obj)}?", () =>
        {
            if (_room.Layers is not null)
            {
                foreach (Layer layer in _room.Layers)
                    layer.InstancesData?.Instances.Remove(obj);
            }
            _room.GameObjects.Remove(obj);
            DataSession.IsModified = true;
            _view.Select(null);
        });
    }

    private void AddNewInstance()
    {
        var objects = DataSession.Data.GameObjects;
        if (objects is null || objects.Count == 0)
        {
            UiHelper.ShowMessage(this, "Add instance", "This game has no objects.");
            return;
        }

        ChooseFromList("Object", objects.Select(o => o?.Name?.Content ?? "(unnamed)").ToList(), index =>
        {
            UndertaleGameObject definition = objects[index];
            void Place(Layer layer)
            {
                var (cx, cy) = _view.ViewCenter;
                AddInstance(new GameObject
                {
                    X = _view.Snap(cx, _room.GridWidth),
                    Y = _view.Snap(cy, _room.GridHeight),
                    ObjectDefinition = definition,
                }, layer);
            }

            if (_room.Layers is not { Count: > 0 })
            {
                Place(null);
                return;
            }
            var layers = _room.Layers.Where(l => l.InstancesData is not null).ToList();
            if (layers.Count == 0)
                UiHelper.ShowMessage(this, "Add instance", "This room has no instance layer to add instances to.");
            else if (layers.Count == 1)
                Place(layers[0]);
            else
                ChooseFromList("Instance layer", layers.Select(l => l.LayerName?.Content ?? $"Layer {l.LayerId}").ToList(), i => Place(layers[i]));
        });
    }

    /// <summary>A dialog with a filter box and a list; returns the index in the original list.</summary>
    private void ChooseFromList(string title, List<string> items, Action<int> onChosen)
    {
        LinearLayout layout = UiHelper.VerticalLayout(this, 12);
        EditText filter = new(this) { Hint = "Filter" };
        filter.SetSingleLine(true);
        layout.AddView(filter);
        ListView list = new(this);
        layout.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, this.Dp(360)));

        List<int> shown = new();
        void Apply()
        {
            string f = filter.Text ?? "";
            shown.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                if (f.Length == 0 || items[i].Contains(f, StringComparison.OrdinalIgnoreCase))
                    shown.Add(i);
            }
            list.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleListItem1, shown.Select(i => items[i]).ToList());
        }
        filter.TextChanged += (_, _) => Apply();
        Apply();

        AlertDialog dialog = new AlertDialog.Builder(this).SetTitle(title)!.SetView(layout)!.SetNegativeButton("Cancel", (_, _) => { })!.Create()!;
        list.ItemClick += (_, e) =>
        {
            dialog.Dismiss();
            onChosen(shown[e.Position]);
        };
        dialog.Show();
    }

    private void ChooseHiddenLayers()
    {
        var layers = _room.Layers?.ToList();
        if (layers is not { Count: > 0 })
        {
            UiHelper.ShowMessage(this, "Layers", "This room doesn't use layers (GameMaker: Studio 1 room).");
            return;
        }
        string[] names = layers.Select(l => $"{l.LayerName?.Content ?? "?"} ({l.LayerType}, depth {l.LayerDepth})").ToArray();
        bool[] visible = layers.Select(l => !_view.HiddenLayers.Contains(l)).ToArray();
        new AlertDialog.Builder(this)
            .SetTitle("Layers shown in the editor")!
            .SetMultiChoiceItems(names, visible, (_, e) =>
            {
                if (e.IsChecked)
                    _view.HiddenLayers.Remove(layers[e.Which]);
                else
                    _view.HiddenLayers.Add(layers[e.Which]);
                _view.Invalidate();
            })!
            .SetPositiveButton("OK", (_, _) => { })!
            .Show();
    }

    #endregion

    #region Modes & tile painting

    private void ChooseMode()
    {
        string[] modes = { "Instances", "Tiles & asset sprites", "Paint tiles (tile layers)" };
        new AlertDialog.Builder(this)
            .SetTitle("Edit mode")!
            .SetSingleChoiceItems(modes, (int)_view.Mode, (sender, e) =>
            {
                ((AlertDialog)sender!).Dismiss();
                SetMode((RoomView.EditMode)e.Which);
            })!
            .Show();
    }

    private void SetMode(RoomView.EditMode mode)
    {
        if (mode == RoomView.EditMode.Paint)
        {
            var layers = _room.Layers?.Where(l => l.TilesData?.Background is not null).ToList();
            if (layers is not { Count: > 0 })
            {
                UiHelper.ShowMessage(this, "Paint tiles", "This room has no tile layers (GameMaker Studio 2 rooms only). " +
                                                           "GMS1 tiles can be moved in \"Tiles & asset sprites\" mode.");
                return;
            }
            ChooseFromList("Tile layer", layers.Select(l => $"{l.LayerName?.Content} ({l.TilesData.Background.Name?.Content})").ToList(), i =>
            {
                _view.PaintLayer = layers[i];
                _view.Mode = mode;
                _view.Select(null);
                InvalidateOptionsMenu();
                PickTile();
            });
            return;
        }
        _view.Mode = mode;
        _view.Select(null);
        InvalidateOptionsMenu();
        UpdateSelectionInfo();
    }

    /// <summary>Shows the paint layer's tileset; tapping a tile selects it for painting.</summary>
    private void PickTile()
    {
        UndertaleBackground tileset = _view.PaintLayer?.TilesData?.Background;
        UndertaleTexturePageItem item = tileset?.Texture;
        if (item is null)
            return;
        global::Android.Graphics.Bitmap bitmap = ImageHelper.GetPageItem(item);
        if (bitmap is null)
        {
            UiHelper.ShowMessage(this, "Pick tile", "Can't show this tileset's image.");
            return;
        }

        int zoom = Math.Clamp((Resources!.DisplayMetrics!.WidthPixels - this.Dp(64)) / Math.Max(1, bitmap.Width), 1, 4);
        global::Android.Graphics.Bitmap scaled = global::Android.Graphics.Bitmap.CreateScaledBitmap(bitmap, bitmap.Width * zoom, bitmap.Height * zoom, false)!;
        ImageView image = new(this);
        image.SetImageBitmap(scaled);
        image.SetScaleType(ImageView.ScaleType.Matrix);
        image.LayoutParameters = new ViewGroup.LayoutParams(scaled.Width, scaled.Height);
        HorizontalScrollView hscroll = new(this);
        hscroll.AddView(image);
        ScrollView scroll = new(this);
        scroll.AddView(hscroll);

        AlertDialog dialog = new AlertDialog.Builder(this)
            .SetTitle($"Tap a tile of {tileset.Name?.Content}")!
            .SetView(scroll)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetNeutralButton("Eraser", (_, _) =>
            {
                _view.PaintTile = 0;
                UpdateSelectionInfo();
            })!
            .Create()!;
        image.Touch += (_, e) =>
        {
            if (e.Event!.Action != MotionEventActions.Up)
            {
                e.Handled = true;
                return;
            }
            int px = (int)(e.Event.GetX() / zoom) - item.TargetX, py = (int)(e.Event.GetY() / zoom) - item.TargetY;
            int cellW = (int)(tileset.GMS2TileWidth + 2 * tileset.GMS2OutputBorderX);
            int cellH = (int)(tileset.GMS2TileHeight + 2 * tileset.GMS2OutputBorderY);
            if (px < 0 || py < 0 || cellW <= 0 || cellH <= 0)
                return;
            uint index = (uint)((py / cellH) * tileset.GMS2TileColumns + Math.Min(px / cellW, (int)tileset.GMS2TileColumns - 1));
            if (index >= tileset.GMS2TileCount)
                return;
            _view.PaintTile = index;
            UpdateSelectionInfo();
            dialog.Dismiss();
            e.Handled = true;
        };
        dialog.Show();
    }

    #endregion

    public override bool OnCreateOptionsMenu(IMenu menu)
    {
        menu.Add(0, 1, 0, "Add instance")!.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, 2, 1, "Fit to screen");
        menu.Add(0, 3, 2, "Snap to grid")!.SetCheckable(true)!.SetChecked(_view?.SnapToGrid ?? true);
        menu.Add(0, 4, 3, "Layers...");
        menu.Add(0, 6, 1, "Edit mode...")!.SetShowAsAction(ShowAsAction.IfRoom);
        if (_view?.Mode == RoomView.EditMode.Paint)
        {
            menu.Add(0, 7, 2, "Pick tile...");
            menu.Add(0, 8, 2, "Eraser");
        }
        menu.Add(0, 5, 4, "Room properties");
        return true;
    }

    public override bool OnOptionsItemSelected(IMenuItem item)
    {
        switch (item.ItemId)
        {
            case 1:
                AddNewInstance();
                return true;
            case 2:
                _view.FitToScreen();
                return true;
            case 3:
                _view.SnapToGrid = !_view.SnapToGrid;
                item.SetChecked(_view.SnapToGrid);
                return true;
            case 4:
                ChooseHiddenLayers();
                return true;
            case 5:
                Navigator.Open(this, _room);
                return true;
            case 6:
                ChooseMode();
                return true;
            case 7:
                PickTile();
                return true;
            case 8:
                _view.PaintTile = 0;
                UpdateSelectionInfo();
                return true;
        }
        return base.OnOptionsItemSelected(item);
    }
}
