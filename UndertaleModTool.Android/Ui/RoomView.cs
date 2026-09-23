using Android.Content;
using Android.Graphics;
using Android.Views;
using UndertaleModLib.Models;
using UndertaleModTool.Android.Services;
using static UndertaleModLib.Models.UndertaleRoom;

namespace UndertaleModTool.Android.Ui;

/// <summary>
/// Draws a room (backgrounds, tiles, instances, sprites; GMS1 and GMS2 layer rooms) and lets the
/// user pan/zoom, select instances and drag them around.
/// </summary>
public sealed class RoomView : global::Android.Views.View
{
    private readonly UndertaleRoom _room;
    private readonly PageCache _pages;
    private readonly Paint _bitmapPaint = new() { FilterBitmap = false };
    private readonly Paint _fillPaint = new() { AntiAlias = false };
    private readonly Paint _outlinePaint = new() { AntiAlias = true, StrokeWidth = 0 };
    private readonly Paint _selectPaint = new() { AntiAlias = true };
    private readonly Paint _labelPaint = new() { AntiAlias = true, Color = Color.White };
    private readonly ScaleGestureDetector _scaleDetector;

    private float _scale = 1, _offsetX, _offsetY;
    private bool _fitted;

    /// <summary>Layers hidden in the editor only (does not change the room).</summary>
    public HashSet<Layer> HiddenLayers { get; } = new();

    public bool SnapToGrid { get; set; } = true;

    /// <summary>Raised when the selection changes or the selected item moves.</summary>
    public event Action SelectionChanged;

    public RoomView(Context context, UndertaleRoom room) : base(context)
    {
        _room = room;
        _pages = new PageCache(PostInvalidate);
        _outlinePaint.SetStyle(Paint.Style.Stroke);
        _outlinePaint.Color = Color.Argb(160, 255, 255, 255);
        _selectPaint.SetStyle(Paint.Style.Stroke);
        _selectPaint.Color = Color.ParseColor("#FFFFC107");
        _labelPaint.TextSize = context.Dp(11);
        _scaleDetector = new ScaleGestureDetector(context, new ScaleListener(this));
        SetBackgroundColor(Color.ParseColor("#FF202020"));
    }

    private bool IsLayerRoom => _room.Layers is { Count: > 0 };

    #region View transform

    public void FitToScreen()
    {
        if (Width == 0 || Height == 0 || _room.Width == 0 || _room.Height == 0)
            return;
        _scale = Math.Min(Width / (float)_room.Width, Height / (float)_room.Height) * 0.95f;
        _offsetX = (Width - _room.Width * _scale) / 2;
        _offsetY = (Height - _room.Height * _scale) / 2;
        Invalidate();
    }

    /// <summary>Room coordinates of the centre of the screen.</summary>
    public (float X, float Y) ViewCenter => ((Width / 2f - _offsetX) / _scale, (Height / 2f - _offsetY) / _scale);

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (!_fitted && w > 0 && h > 0)
        {
            _fitted = true;
            FitToScreen();
        }
    }

    #endregion

    #region Drawing

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.Save();
        canvas.Translate(_offsetX, _offsetY);
        canvas.Scale(_scale, _scale);

        // Room area
        if (IsLayerRoom)
        {
            _fillPaint.Color = Color.Black;
            canvas.DrawRect(0, 0, _room.Width, _room.Height, _fillPaint);
            foreach (Layer layer in _room.Layers.Where(l => l is not null).OrderByDescending(l => l.LayerDepth))
            {
                if (layer.IsVisible && !HiddenLayers.Contains(layer))
                    DrawLayer(canvas, layer);
            }
        }
        else
        {
            _fillPaint.Color = _room.DrawBackgroundColor ? FromGmColor(_room.BackgroundColor, opaque: true) : Color.Black;
            canvas.DrawRect(0, 0, _room.Width, _room.Height, _fillPaint);
            DrawLegacy(canvas);
        }

        // Room bounds and selection
        _outlinePaint.StrokeWidth = 1 / _scale;
        canvas.DrawRect(0, 0, _room.Width, _room.Height, _outlinePaint);
        if (SelectedItem is not null)
        {
            _selectPaint.StrokeWidth = 2 / _scale;
            canvas.DrawRect(ItemBounds(SelectedItem), _selectPaint);
        }
        if (Mode == EditMode.Paint && PaintLayer?.TilesData?.Background is { } tileset)
            DrawTileGrid(canvas, PaintLayer, tileset);
        canvas.Restore();
    }

    private void DrawLegacy(Canvas canvas)
    {
        foreach (Background bg in _room.Backgrounds)
        {
            if (bg is { Enabled: true, Foreground: false })
                DrawRoomBackground(canvas, bg.BackgroundDefinition?.Texture, bg.X, bg.Y, bg.TiledHorizontally, bg.TiledVertically, bg.Stretch);
        }

        // Tiles and instances interleave by depth (higher depth is drawn first).
        var drawables = new List<(int Depth, int Order, object Item)>();
        int order = 0;
        foreach (Tile tile in _room.Tiles)
            drawables.Add((tile.TileDepth, order++, tile));
        foreach (GameObject obj in _room.GameObjects)
            drawables.Add((obj.ObjectDefinition?.Depth ?? 0, order++, obj));
        foreach (var (_, _, item) in drawables.OrderByDescending(d => d.Depth).ThenBy(d => d.Order))
        {
            if (item is Tile tile)
                DrawTile(canvas, tile);
            else
                DrawInstance(canvas, (GameObject)item);
        }

        foreach (Background bg in _room.Backgrounds)
        {
            if (bg is { Enabled: true, Foreground: true })
                DrawRoomBackground(canvas, bg.BackgroundDefinition?.Texture, bg.X, bg.Y, bg.TiledHorizontally, bg.TiledVertically, bg.Stretch);
        }
    }

    private void DrawLayer(Canvas canvas, Layer layer)
    {
        switch (layer.Data)
        {
            case Layer.LayerBackgroundData bg when bg.Visible:
                if (bg.Sprite is null)
                {
                    _fillPaint.Color = FromGmColor(bg.Color, opaque: false);
                    canvas.DrawRect(0, 0, _room.Width, _room.Height, _fillPaint);
                }
                else
                {
                    DrawRoomBackground(canvas, bg.Sprite.Textures.FirstOrDefault()?.Texture, (int)layer.XOffset, (int)layer.YOffset,
                                       bg.TiledHorizontally, bg.TiledVertically, bg.Stretch);
                }
                break;
            case Layer.LayerInstancesData instances:
                foreach (GameObject obj in instances.Instances)
                    DrawInstance(canvas, obj);
                break;
            case Layer.LayerTilesData tiles:
                DrawTileLayer(canvas, layer, tiles);
                break;
            case Layer.LayerAssetsData assets:
                if (assets.LegacyTiles is not null)
                {
                    foreach (Tile tile in assets.LegacyTiles)
                        DrawTile(canvas, tile);
                }
                if (assets.Sprites is not null)
                {
                    foreach (SpriteInstance sprite in assets.Sprites)
                        DrawSprite(canvas, sprite.Sprite, sprite.WrappedFrameIndex, sprite.X, sprite.Y, sprite.ScaleX, sprite.ScaleY, sprite.Rotation);
                }
                break;
        }
    }

    private void DrawRoomBackground(Canvas canvas, UndertaleTexturePageItem item, int x, int y, bool tileX, bool tileY, bool stretch)
    {
        if (item is null || item.BoundingWidth == 0 || item.BoundingHeight == 0)
            return;
        float w = item.BoundingWidth, h = item.BoundingHeight;
        if (stretch)
        {
            DrawPageItem(canvas, item, x, y, _room.Width / w, _room.Height / h);
            return;
        }

        float startX = tileX ? x - (float)Math.Ceiling(x / w) * w : x;
        float startY = tileY ? y - (float)Math.Ceiling(y / h) * h : y;
        float endX = tileX ? _room.Width : startX + 1;
        float endY = tileY ? _room.Height : startY + 1;
        for (float py = startY; py < endY; py += h)
            for (float px = startX; px < endX; px += w)
                DrawPageItem(canvas, item, px, py, 1, 1);
    }

    private void DrawTile(Canvas canvas, Tile tile)
    {
        UndertaleTexturePageItem item = tile.spriteMode
            ? tile.SpriteDefinition?.Textures.FirstOrDefault()?.Texture
            : tile.BackgroundDefinition?.Texture;
        if (item is null || !_pages.TryGet(item.TexturePage, out Bitmap page, out float factor))
            return;

        // Tile source coordinates are relative to the tileset image.
        int sx = item.SourceX + tile.SourceX - item.TargetX;
        int sy = item.SourceY + tile.SourceY - item.TargetY;
        canvas.Save();
        canvas.Translate(tile.X, tile.Y);
        canvas.Scale(tile.ScaleX, tile.ScaleY);
        canvas.DrawBitmap(page, ScaledRect(sx, sy, (int)tile.Width, (int)tile.Height, factor),
                          new RectF(0, 0, tile.Width, tile.Height), _bitmapPaint);
        canvas.Restore();
    }

    private void DrawTileGrid(Canvas canvas, Layer layer, UndertaleBackground tileset)
    {
        var tiles = layer.TilesData;
        float w = tileset.GMS2TileWidth, h = tileset.GMS2TileHeight;
        if (w <= 0 || h <= 0 || tiles.TileData is null)
            return;
        _outlinePaint.StrokeWidth = 1 / _scale;
        _outlinePaint.Color = Color.Argb(70, 255, 255, 255);
        float right = layer.XOffset + tiles.TilesX * w, bottom = layer.YOffset + tiles.TilesY * h;
        for (int x = 0; x <= tiles.TilesX; x++)
            canvas.DrawLine(layer.XOffset + x * w, layer.YOffset, layer.XOffset + x * w, bottom, _outlinePaint);
        for (int y = 0; y <= tiles.TilesY; y++)
            canvas.DrawLine(layer.XOffset, layer.YOffset + y * h, right, layer.YOffset + y * h, _outlinePaint);
        _outlinePaint.Color = Color.Argb(160, 255, 255, 255);
    }

    private void DrawTileLayer(Canvas canvas, Layer layer, Layer.LayerTilesData tiles)
    {
        UndertaleBackground tileset = tiles.Background;
        UndertaleTexturePageItem item = tileset?.Texture;
        if (item is null || tiles.TileData is null || tileset.GMS2TileColumns == 0 ||
            !_pages.TryGet(item.TexturePage, out Bitmap page, out float factor))
        {
            return;
        }

        int w = (int)tileset.GMS2TileWidth, h = (int)tileset.GMS2TileHeight;
        int borderX = (int)tileset.GMS2OutputBorderX, borderY = (int)tileset.GMS2OutputBorderY;
        int columns = (int)tileset.GMS2TileColumns;
        RectF dst = new();
        for (int ty = 0; ty < tiles.TileData.Length; ty++)
        {
            uint[] row = tiles.TileData[ty];
            if (row is null)
                continue;
            for (int tx = 0; tx < row.Length; tx++)
            {
                uint id = row[tx];
                uint index = id & 0x7FFFF;
                if (index == 0 || index >= tileset.GMS2TileCount)
                    continue;

                int col = (int)(index % columns), rowIndex = (int)(index / columns);
                int sx = item.SourceX + (col + 1) * borderX + col * (w + borderX);
                int sy = item.SourceY + (rowIndex + 1) * borderY + rowIndex * (h + borderY);
                float x = layer.XOffset + tx * w, y = layer.YOffset + ty * h;

                uint flags = id >> 28;
                if ((flags & 7) == 0)
                {
                    dst.Set(x, y, x + w, y + h);
                    canvas.DrawBitmap(page, ScaledRect(sx, sy, w, h, factor), dst, _bitmapPaint);
                    continue;
                }

                // bit 0: mirror, bit 1: flip, bit 2: rotate 90° (applied after mirror/flip)
                canvas.Save();
                canvas.Translate(x + w / 2f, y + h / 2f);
                if ((flags & 4) != 0)
                    canvas.Rotate(90);
                canvas.Scale((flags & 1) != 0 ? -1 : 1, (flags & 2) != 0 ? -1 : 1);
                dst.Set(-w / 2f, -h / 2f, w / 2f, h / 2f);
                canvas.DrawBitmap(page, ScaledRect(sx, sy, w, h, factor), dst, _bitmapPaint);
                canvas.Restore();
            }
        }
    }

    private void DrawInstance(Canvas canvas, GameObject obj)
    {
        UndertaleSprite sprite = obj.ObjectDefinition?.Sprite;
        if (sprite is null || sprite.Textures.Count == 0)
        {
            // Objects without a sprite: draw a marker, like GameMaker's room editor.
            _fillPaint.Color = Color.Argb(160, 80, 160, 255);
            canvas.DrawRect(obj.X - 8, obj.Y - 8, obj.X + 8, obj.Y + 8, _fillPaint);
            if (_scale >= 0.75f)
            {
                canvas.Save();
                canvas.Translate(obj.X - 8, obj.Y - 10);
                canvas.Scale(1 / _scale, 1 / _scale);
                canvas.DrawText(obj.ObjectDefinition?.Name?.Content ?? "?", 0, 0, _labelPaint);
                canvas.Restore();
            }
            return;
        }
        DrawSprite(canvas, sprite, obj.ImageIndex, obj.X, obj.Y, obj.ScaleX, obj.ScaleY, obj.Rotation);
    }

    private void DrawSprite(Canvas canvas, UndertaleSprite sprite, int frame, float x, float y, float scaleX, float scaleY, float rotation)
    {
        if (sprite is null || sprite.Textures.Count == 0)
            return;
        UndertaleTexturePageItem item = sprite.Textures[Math.Clamp(frame, 0, sprite.Textures.Count - 1)]?.Texture;
        if (item is null)
            return;
        canvas.Save();
        canvas.Translate(x, y);
        if (rotation != 0)
            canvas.Rotate(-rotation);
        canvas.Scale(scaleX, scaleY);
        canvas.Translate(-sprite.OriginXWrapper, -sprite.OriginYWrapper);
        DrawPageItem(canvas, item, 0, 0, 1, 1);
        canvas.Restore();
    }

    /// <summary>Draws a texture page item with its bounding box's top-left corner at (x, y).</summary>
    private void DrawPageItem(Canvas canvas, UndertaleTexturePageItem item, float x, float y, float scaleX, float scaleY)
    {
        if (item?.TexturePage is null || !_pages.TryGet(item.TexturePage, out Bitmap page, out float factor))
            return;
        RectF dst = new(x + item.TargetX * scaleX, y + item.TargetY * scaleY,
                        x + (item.TargetX + item.TargetWidth) * scaleX, y + (item.TargetY + item.TargetHeight) * scaleY);
        canvas.DrawBitmap(page, ScaledRect(item.SourceX, item.SourceY, item.SourceWidth, item.SourceHeight, factor), dst, _bitmapPaint);
    }

    private static Rect ScaledRect(int x, int y, int w, int h, float factor)
        => factor == 1
            ? new Rect(x, y, x + w, y + h)
            : new Rect((int)(x / factor), (int)(y / factor), (int)((x + w) / factor), (int)((y + h) / factor));

    /// <summary>GameMaker colors are 0xAABBGGRR.</summary>
    private static Color FromGmColor(uint color, bool opaque)
        => Color.Argb(opaque ? 255 : (int)(color >> 24), (int)(color & 0xFF), (int)((color >> 8) & 0xFF), (int)((color >> 16) & 0xFF));

    #endregion

    #region Selection & editing

    public enum EditMode
    {
        /// <summary>Select and move object instances.</summary>
        Instances,
        /// <summary>Select and move tiles (GMS1 tiles, legacy asset tiles) and asset-layer sprites.</summary>
        Tiles,
        /// <summary>Paint tiles on a GMS2 tile layer.</summary>
        Paint,
    }

    public EditMode Mode { get; set; } = EditMode.Instances;

    /// <summary>Tile layer to paint on (Paint mode).</summary>
    public Layer PaintLayer { get; set; }

    /// <summary>Tile index to paint (0 erases). May include mirror/flip/rotate flags in the top bits.</summary>
    public uint PaintTile { get; set; }

    /// <summary>The selected GameObject, Tile or SpriteInstance.</summary>
    public object SelectedItem { get; private set; }

    public GameObject SelectedInstance => SelectedItem as GameObject;

    /// <summary>All instances in draw order (bottom first).</summary>
    public IEnumerable<GameObject> InstancesInDrawOrder()
    {
        if (IsLayerRoom)
        {
            return _room.Layers.Where(l => l is { IsVisible: true, InstancesData: not null } && !HiddenLayers.Contains(l))
                                .OrderByDescending(l => l.LayerDepth)
                                .SelectMany(l => l.InstancesData.Instances);
        }
        return _room.GameObjects.Select((o, i) => (o, i))
                                .OrderByDescending(p => p.o.ObjectDefinition?.Depth ?? 0).ThenBy(p => p.i)
                                .Select(p => p.o);
    }

    /// <summary>All tiles and asset sprites in draw order (bottom first).</summary>
    public IEnumerable<object> TilesInDrawOrder()
    {
        if (!IsLayerRoom)
            return _room.Tiles.Select((t, i) => (t, i)).OrderByDescending(p => p.t.TileDepth).ThenBy(p => p.i).Select(p => (object)p.t);
        return _room.Layers.Where(l => l is { IsVisible: true, AssetsData: not null } && !HiddenLayers.Contains(l))
                           .OrderByDescending(l => l.LayerDepth)
                           .SelectMany(l => (l.AssetsData.LegacyTiles?.Cast<object>() ?? Enumerable.Empty<object>())
                                .Concat(l.AssetsData.Sprites?.Cast<object>() ?? Enumerable.Empty<object>()));
    }

    public RectF InstanceBounds(GameObject obj) => SpriteBounds(obj.ObjectDefinition?.Sprite, obj.X, obj.Y, obj.ScaleX, obj.ScaleY, obj.Rotation);

    private static RectF SpriteBounds(UndertaleSprite sprite, float x, float y, float scaleX, float scaleY, float rotation)
    {
        if (sprite is null || sprite.Textures.Count == 0)
            return new RectF(x - 8, y - 8, x + 8, y + 8);
        float x1 = x - sprite.OriginXWrapper * scaleX, y1 = y - sprite.OriginYWrapper * scaleY;
        float x2 = x1 + sprite.Width * scaleX, y2 = y1 + sprite.Height * scaleY;
        RectF r = new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
        if (rotation % 360 != 0)
        {
            using Matrix m = new();
            m.SetRotate(-rotation, x, y);
            m.MapRect(r);
        }
        return r;
    }

    public RectF ItemBounds(object item) => item switch
    {
        GameObject obj => InstanceBounds(obj),
        Tile tile => new RectF(Math.Min(tile.X, tile.X + tile.Width * tile.ScaleX), Math.Min(tile.Y, tile.Y + tile.Height * tile.ScaleY),
                               Math.Max(tile.X, tile.X + tile.Width * tile.ScaleX), Math.Max(tile.Y, tile.Y + tile.Height * tile.ScaleY)),
        SpriteInstance sprite => SpriteBounds(sprite.Sprite, sprite.X, sprite.Y, sprite.ScaleX, sprite.ScaleY, sprite.Rotation),
        _ => new RectF(),
    };

    private static (int X, int Y) PositionOf(object item) => item switch
    {
        GameObject obj => (obj.X, obj.Y),
        Tile tile => (tile.X, tile.Y),
        SpriteInstance sprite => (sprite.X, sprite.Y),
        _ => (0, 0),
    };

    private static void MoveTo(object item, int x, int y)
    {
        switch (item)
        {
            case GameObject obj:
                obj.X = x;
                obj.Y = y;
                break;
            case Tile tile:
                tile.X = x;
                tile.Y = y;
                break;
            case SpriteInstance sprite:
                sprite.X = x;
                sprite.Y = y;
                break;
        }
    }

    public object HitTest(float roomX, float roomY)
    {
        IEnumerable<object> candidates = Mode == EditMode.Tiles ? TilesInDrawOrder() : InstancesInDrawOrder();
        return candidates.LastOrDefault(o => ItemBounds(o).Contains(roomX, roomY));
    }

    public void Select(object item)
    {
        SelectedItem = item;
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public int Snap(float value, double grid)
        => SnapToGrid && grid >= 1 ? (int)(Math.Round(value / grid) * grid) : (int)Math.Round(value);

    /// <summary>Paints <see cref="PaintTile"/> into the tile cell under a room position.</summary>
    private bool PaintAt(float roomX, float roomY)
    {
        if (PaintLayer?.TilesData is not { TileData: not null } tiles || tiles.Background is null)
            return false;
        int w = (int)tiles.Background.GMS2TileWidth, h = (int)tiles.Background.GMS2TileHeight;
        if (w <= 0 || h <= 0)
            return false;
        int cx = (int)Math.Floor((roomX - PaintLayer.XOffset) / w), cy = (int)Math.Floor((roomY - PaintLayer.YOffset) / h);
        if (cy < 0 || cy >= tiles.TileData.Length || tiles.TileData[cy] is not { } row || cx < 0 || cx >= row.Length)
            return false;
        if (row[cx] == PaintTile)
            return false;
        row[cx] = PaintTile;
        DataSession.IsModified = true;
        return true;
    }

    #endregion

    #region Touch

    private float _downX, _downY, _lastX, _lastY;
    private bool _dragItem, _moved, _multiTouch;
    private float _dragStartX, _dragStartY;

    public override bool OnTouchEvent(MotionEvent e)
    {
        _scaleDetector.OnTouchEvent(e);
        float slop = ViewConfiguration.Get(Context)!.ScaledTouchSlop;
        float rx = (e.GetX() - _offsetX) / _scale, ry = (e.GetY() - _offsetY) / _scale;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = e.GetX();
                _downY = e.GetY();
                _lastX = e.GetX();
                _lastY = e.GetY();
                _moved = _multiTouch = false;
                _dragItem = Mode != EditMode.Paint && SelectedItem is not null && ItemBounds(SelectedItem).Contains(rx, ry);
                if (_dragItem)
                    (_dragStartX, _dragStartY) = PositionOf(SelectedItem);
                if (Mode == EditMode.Paint && PaintAt(rx, ry))
                    Invalidate();
                return true;

            case MotionEventActions.PointerDown:
                _multiTouch = true;
                _dragItem = false;
                return true;

            case MotionEventActions.Move:
                if (_multiTouch || _scaleDetector.IsInProgress)
                    return true;
                if (Mode == EditMode.Paint)
                {
                    if (PaintAt(rx, ry))
                        Invalidate();
                    return true;
                }
                if (!_moved && Math.Abs(e.GetX() - _downX) < slop && Math.Abs(e.GetY() - _downY) < slop)
                    return true;
                _moved = true;
                if (_dragItem)
                {
                    float dx = (e.GetX() - _downX) / _scale, dy = (e.GetY() - _downY) / _scale;
                    int nx = Snap(_dragStartX + dx, _room.GridWidth), ny = Snap(_dragStartY + dy, _room.GridHeight);
                    if ((nx, ny) != PositionOf(SelectedItem))
                    {
                        MoveTo(SelectedItem, nx, ny);
                        DataSession.IsModified = true;
                        SelectionChanged?.Invoke();
                    }
                }
                else
                {
                    _offsetX += e.GetX() - _lastX;
                    _offsetY += e.GetY() - _lastY;
                }
                _lastX = e.GetX();
                _lastY = e.GetY();
                Invalidate();
                return true;

            case MotionEventActions.Up:
                if (!_moved && !_multiTouch && Mode != EditMode.Paint)
                    Select(HitTest(rx, ry));
                return true;
        }
        return base.OnTouchEvent(e);
    }

    private sealed class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly RoomView _view;
        private float _focusX, _focusY;

        public ScaleListener(RoomView view) => _view = view;

        public override bool OnScaleBegin(ScaleGestureDetector detector)
        {
            _focusX = detector.FocusX;
            _focusY = detector.FocusY;
            return true;
        }

        public override bool OnScale(ScaleGestureDetector detector)
        {
            // Two-finger gestures zoom around the focus point and pan with it.
            float newScale = Math.Clamp(_view._scale * detector.ScaleFactor, 0.05f, 32f);
            float fx = detector.FocusX, fy = detector.FocusY;
            _view._offsetX = fx - (_focusX - _view._offsetX) * (newScale / _view._scale);
            _view._offsetY = fy - (_focusY - _view._offsetY) * (newScale / _view._scale);
            _view._scale = newScale;
            _focusX = fx;
            _focusY = fy;
            _view.Invalidate();
            return true;
        }
    }

    #endregion

    protected override void OnDetachedFromWindow()
    {
        base.OnDetachedFromWindow();
        _pages.Clear();
    }

    /// <summary>
    /// Decoded texture pages, loaded in the background and kept in a small LRU cache.
    /// </summary>
    private sealed class PageCache
    {
        private const int MaxPages = 8;
        private readonly Dictionary<UndertaleEmbeddedTexture, (Bitmap Bitmap, float Factor)> _pages = new();
        private readonly LinkedList<UndertaleEmbeddedTexture> _lru = new();
        private readonly HashSet<UndertaleEmbeddedTexture> _loading = new();
        private readonly HashSet<UndertaleEmbeddedTexture> _failed = new();
        private readonly Action _onLoaded;

        public PageCache(Action onLoaded) => _onLoaded = onLoaded;

        /// <summary>Returns a page if decoded; otherwise starts decoding it and returns false.</summary>
        public bool TryGet(UndertaleEmbeddedTexture texture, out Bitmap bitmap, out float factor)
        {
            bitmap = null;
            factor = 1;
            if (texture is null)
                return false;
            lock (_pages)
            {
                if (_pages.TryGetValue(texture, out var entry))
                {
                    _lru.Remove(texture);
                    _lru.AddFirst(texture);
                    (bitmap, factor) = entry;
                    return true;
                }
                if (_failed.Contains(texture) || !_loading.Add(texture))
                    return false;
            }

            Task.Run(() =>
            {
                Bitmap decoded = null;
                try
                {
                    decoded = ImageHelper.GetPage(texture, null);
                }
                catch
                {
                    // Unsupported format; drawn as empty.
                }

                lock (_pages)
                {
                    _loading.Remove(texture);
                    if (decoded is null)
                    {
                        _failed.Add(texture);
                        return;
                    }
                    int realWidth = texture.TextureData?.Image?.Width ?? decoded.Width;
                    _pages[texture] = (decoded, realWidth / (float)decoded.Width);
                    _lru.AddFirst(texture);
                    while (_lru.Count > MaxPages)
                    {
                        UndertaleEmbeddedTexture evict = _lru.Last!.Value;
                        _lru.RemoveLast();
                        // Not recycled here: the UI thread may be drawing it right now. The GC frees it.
                        _pages.Remove(evict);
                    }
                }
                _onLoaded();
            });
            return false;
        }

        public void Clear()
        {
            lock (_pages)
            {
                foreach (var (bitmap, _) in _pages.Values)
                    bitmap.Recycle();
                _pages.Clear();
                _lru.Clear();
            }
        }
    }
}
