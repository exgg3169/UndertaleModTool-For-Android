using System.Collections;
using System.Text;
using System.Text.Json.Nodes;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModTool.Core.Assets;
using UndertaleModTool.Core.Data;
using UndertaleModTool.Core.Imaging;
using UndertaleModTool.Core.Scripting;
using static UndertaleModLib.Models.UndertaleRoom;

namespace UndertaleModTool.Core.Mcp;

/// <summary>
/// The UndertaleModTool tools exposed over MCP.
/// </summary>
public static class UmtTools
{
    public const string Instructions =
        "You are connected to UndertaleModTool running on an Android device, editing a GameMaker data file " +
        "(data.win / game.droid / game.unx ...). Start with get_status. Resources are grouped in categories " +
        "(Sprites, Rooms, GameObjects, Code, Strings, Sounds, ...): use list_resources / get_resource to explore and " +
        "set_resource_property to edit. Code entries are named like gml_Script_x, gml_Object_obj_x_Create_0 and " +
        "gml_GlobalScript_x: use decompile_code to read GML and import_code to change it (creates missing entries). " +
        "run_csharp runs a UTMT C# script (same API as the desktop UndertaleModTool: Data, ScriptMessage, GetDecompiledText, " +
        "and for code changes: var g = new CodeImportGroup(Data); g.QueueReplace(\"gml_Script_x\", gml); g.Import();). " +
        "Changes stay in memory until save_data_file is called; tell the user before saving over their file.";

    public static McpServer Create(IUmtHost host, string version = "1.0.0")
    {
        McpServer server = new() { Instructions = Instructions, Version = version };
        foreach (McpTool tool in Build(host))
            server.Add(tool);
        return server;
    }

    #region Schema helpers

    private static JsonObject Prop(string type, string description, JsonNode extra = null)
    {
        JsonObject p = new() { ["type"] = type, ["description"] = description };
        if (extra is JsonObject o)
        {
            foreach (var (k, v) in o)
                p[k] = v?.DeepClone();
        }
        return p;
    }

    private static JsonObject Str(string d) => Prop("string", d);
    private static JsonObject Int(string d) => Prop("integer", d);
    private static JsonObject Num(string d) => Prop("number", d);
    private static JsonObject Bool(string d) => Prop("boolean", d);
    private static JsonObject Enum(string d, params string[] values) => Prop("string", d, new JsonObject { ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) });
    private static JsonObject Any(string d) => new() { ["description"] = d };

    private static JsonObject Schema(params (string Name, JsonObject Prop, bool Required)[] props)
    {
        JsonObject properties = new();
        JsonArray required = new();
        foreach (var (name, prop, req) in props)
        {
            properties[name] = prop.Parent is null ? prop : prop.DeepClone();
            if (req)
                required.Add(name);
        }
        JsonObject schema = new() { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static string S(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue(out string s) ? s : a[key]?.ToString();
    private static int? I(JsonObject a, string key) => a[key] is null ? null : int.Parse(a[key]!.ToString());
    private static float? F(JsonObject a, string key) => a[key] is null ? null : float.Parse(a[key]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    private static bool B(JsonObject a, string key, bool fallback) => a[key] is null ? fallback : bool.Parse(a[key]!.ToString());
    private static string Required(JsonObject a, string key) => S(a, key) ?? throw new ArgumentException($"\"{key}\" is required.");

    #endregion

    private static UndertaleData RequireData(IUmtHost host)
        => host.Data ?? throw new InvalidOperationException("No data file is loaded. Open one in the app, or use open_data_file.");

    private static IEnumerable<McpTool> Build(IUmtHost host)
    {
        var nameOrIndex = new[]
        {
            ("name", Str("Resource name (or string content for Strings)."), false),
            ("index", Int("Index in the category (alternative to name)."), false),
        };

        // ---------------- Status & files ----------------

        yield return new McpTool("get_status",
            "Shows the loaded data file (game name, GameMaker version, unsaved changes) and the resource categories with their item counts.",
            Schema(), _ =>
            {
                UndertaleData data = host.Data;
                JsonObject status = new() { ["loaded"] = data is not null, ["file"] = host.DataName, ["modified"] = host.IsModified, ["work_directory"] = host.WorkDirectory };
                if (data is not null)
                {
                    var gen = data.GeneralInfo;
                    status["game"] = gen?.DisplayName?.Content ?? gen?.Name?.Content;
                    status["gamemaker_version"] = gen is null ? null : $"{gen.Major}.{gen.Minor}.{gen.Release}.{gen.Build}";
                    status["bytecode_version"] = gen?.BytecodeVersion;
                    status["is_gms2"] = data.IsGameMaker2();
                    status["is_yyc"] = data.IsYYC();
                    JsonObject counts = new();
                    foreach (var p in ObjectInspector.CategoryProperties(data))
                        counts[p.Name] = ((IList)p.GetValue(data)).Count;
                    status["categories"] = counts;
                }
                return ToolResult.Json(status);
            }, ReadOnly: true);

        yield return new McpTool("open_data_file",
            "Opens a GameMaker data file from a path on the device (e.g. /storage/emulated/0/Download/data.win), replacing the currently loaded one (unsaved changes are lost).",
            Schema(("path", Str("File path on the device."), true)), a =>
            {
                host.LoadDataFile(Required(a, "path"));
                return ToolResult.Text($"Loaded {host.DataName}.");
            });

        yield return new McpTool("save_data_file",
            "Saves the data file. Without a path it overwrites the file it was opened from; with a path it writes a new file there.",
            Schema(("path", Str("Optional destination path on the device."), false)), a =>
            {
                RequireData(host);
                return ToolResult.Text("Saved to " + host.SaveDataFile(S(a, "path")));
            });

        yield return new McpTool("list_files",
            "Lists files in a directory on the device (default: the tool's work directory, where exports and script output go).",
            Schema(("path", Str("Directory path; default is the work directory."), false)), a =>
            {
                string dir = S(a, "path") ?? host.WorkDirectory;
                DirectoryInfo info = new(dir);
                if (!info.Exists)
                    return ToolResult.Error($"Directory not found: {dir}");
                StringBuilder sb = new($"{info.FullName}\n");
                foreach (var d in info.GetDirectories().OrderBy(d => d.Name))
                    sb.AppendLine($"[dir]  {d.Name}");
                foreach (var f in info.GetFiles().OrderBy(f => f.Name))
                    sb.AppendLine($"{f.Length,10}  {f.Name}");
                return ToolResult.Text(sb.ToString());
            }, ReadOnly: true);

        yield return new McpTool("read_file",
            "Reads a file on the device: text files are returned as text, PNG images as images, other files as base64 (up to 4 MB).",
            Schema(("path", Str("File path."), true)), a =>
            {
                string path = Required(a, "path");
                byte[] bytes = File.ReadAllBytes(path);
                if (PngDecoder.IsPng(bytes))
                    return new ToolResult().AddText($"{path} ({bytes.Length} bytes)").AddImage(bytes);
                if (bytes.Length > 4 * 1024 * 1024)
                    return ToolResult.Error($"File is too large ({bytes.Length} bytes).");
                bool text = bytes.Take(8192).All(b => b >= 9 && b != 0);
                return ToolResult.Text(text ? Encoding.UTF8.GetString(bytes) : "base64:" + Convert.ToBase64String(bytes));
            }, ReadOnly: true);

        yield return new McpTool("write_file",
            "Writes a file on the device (text, or base64 with the \"base64:\" prefix). Useful to prepare files for scripts.",
            Schema(("path", Str("File path."), true), ("content", Str("Text, or \"base64:...\" for binary."), true)), a =>
            {
                string path = Required(a, "path");
                string content = Required(a, "content");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (content.StartsWith("base64:", StringComparison.Ordinal))
                    File.WriteAllBytes(path, Convert.FromBase64String(content[7..]));
                else
                    File.WriteAllText(path, content);
                return ToolResult.Text($"Wrote {new FileInfo(path).Length} bytes to {path}");
            });

        // ---------------- Generic resources ----------------

        yield return new McpTool("list_resources",
            "Lists items of a resource category with their indexes, optionally filtered by a case-insensitive substring of the name.",
            Schema(("category", Str("Category, e.g. Sprites, Rooms, GameObjects, Code, Strings, Sounds, Scripts, Fonts, Backgrounds, Variables, Functions."), true),
                   ("filter", Str("Only names containing this text."), false),
                   ("offset", Int("Skip this many matches (paging)."), false),
                   ("limit", Int("Maximum results (default 200)."), false)), a =>
            {
                IList list = ObjectInspector.GetCategory(RequireData(host), Required(a, "category"));
                string filter = S(a, "filter");
                int offset = I(a, "offset") ?? 0, limit = I(a, "limit") ?? 200;
                StringBuilder sb = new();
                int matches = 0, shown = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    string name = ObjectInspector.NameOf(list[i]) ?? "(null)";
                    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (matches++ < offset || shown >= limit)
                        continue;
                    shown++;
                    sb.AppendLine($"{i}: {(name.Length > 200 ? name[..200] + "..." : name).Replace("\n", "\\n")}");
                }
                sb.Insert(0, $"{matches} match(es) of {list.Count} items; showing {shown} from offset {offset}.\n");
                return ToolResult.Text(sb.ToString());
            }, ReadOnly: true);

        yield return new McpTool("get_resource",
            "Returns a resource's properties as JSON. Use path to drill into nested members (e.g. \"Textures[0].Texture\", \"Layers[2].InstancesData.Instances\", \"Events[0][0].Actions\").",
            Schema(("category", Str("Category name."), true), nameOrIndex[0], nameOrIndex[1],
                   ("path", Str("Optional member path inside the resource."), false),
                   ("depth", Int("How many levels of nested objects/lists to expand (default 1)."), false)), a =>
            {
                IList list = ObjectInspector.GetCategory(RequireData(host), Required(a, "category"));
                object item = ObjectInspector.FindItem(list, S(a, "name"), I(a, "index"), out int index);
                object target = ObjectInspector.Navigate(item, S(a, "path"));
                JsonNode json = ObjectInspector.Describe(target, I(a, "depth") ?? 1);
                return ToolResult.Text($"#{index} {ObjectInspector.NameOf(item)}\n" + (json?.ToJsonString(McpServer.PrettyJson) ?? "null"));
            }, ReadOnly: true);

        yield return new McpTool("set_resource_property",
            "Sets a property of a resource (or of a nested member via a path like \"Textures[0].Texture.TargetX\"). Values: numbers, booleans, enum names, strings; for references to other resources give the resource name (null to clear).",
            Schema(("category", Str("Category name."), true), nameOrIndex[0], nameOrIndex[1],
                   ("path", Str("Property path, e.g. \"Width\", \"Sprite\", \"Layers[0].XOffset\"."), true),
                   ("value", Any("New value."), true)), a =>
            {
                UndertaleData data = RequireData(host);
                IList list = ObjectInspector.GetCategory(data, Required(a, "category"));
                object item = ObjectInspector.FindItem(list, S(a, "name"), I(a, "index"), out _);
                JsonNode result = ObjectInspector.SetValue(data, item, Required(a, "path"), a["value"]);
                host.MarkModified();
                return ToolResult.Text($"{Required(a, "path")} = {result?.ToJsonString() ?? "null"}");
            });

        yield return new McpTool("set_string",
            "Changes the text of an entry in the string table (Strings category) by index. All resources using that string change too.",
            Schema(("index", Int("String index."), true), ("value", Str("New text."), true)), a =>
            {
                UndertaleData data = RequireData(host);
                int index = I(a, "index")!.Value;
                data.Strings[index].Content = Required(a, "value");
                host.MarkModified();
                return ToolResult.Text($"String {index} updated.");
            });

        // ---------------- Code ----------------

        yield return new McpTool("decompile_code",
            "Decompiles a code entry to GML (e.g. gml_Object_obj_player_Step_0, gml_Script_scr_x, gml_GlobalScript_x, gml_RoomCC_room_0_Create).",
            Schema(("name", Str("Code entry name."), true)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleCode code = data.Code.ByName(Required(a, "name")) ?? throw new ArgumentException($"No code entry named \"{S(a, "name")}\".");
                return ToolResult.Text(CodeTools.Decompile(data, code, host.DecompileContext));
            }, ReadOnly: true);

        yield return new McpTool("disassemble_code",
            "Disassembles a code entry to GameMaker bytecode assembly.",
            Schema(("name", Str("Code entry name."), true)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleCode code = data.Code.ByName(Required(a, "name")) ?? throw new ArgumentException($"No code entry named \"{S(a, "name")}\".");
                return ToolResult.Text(CodeTools.Disassemble(data, code));
            }, ReadOnly: true);

        yield return new McpTool("import_code",
            "Compiles GML into a code entry. mode: replace (default; creates the entry and links object events/scripts if missing), append, prepend, find_replace (replace text \"search\" in the decompiled code with gml), regex_find_replace.",
            Schema(("name", Str("Code entry name, e.g. gml_Object_obj_player_Create_0."), true),
                   ("gml", Str("GML source (or the replacement text for find_replace)."), true),
                   ("mode", Enum("How to import.", "replace", "append", "prepend", "find_replace", "regex_find_replace"), false),
                   ("search", Str("Text/regex to find, for find_replace modes."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                CodeTools.ImportMode mode = (S(a, "mode") ?? "replace") switch
                {
                    "append" => CodeTools.ImportMode.Append,
                    "prepend" => CodeTools.ImportMode.Prepend,
                    "find_replace" => CodeTools.ImportMode.FindReplace,
                    "regex_find_replace" => CodeTools.ImportMode.RegexFindReplace,
                    _ => CodeTools.ImportMode.Replace,
                };
                string error = CodeTools.Import(data, Required(a, "name"), mode, Required(a, "gml"), S(a, "search"), host.RunOnMainThread);
                if (error is not null)
                    return ToolResult.Error("Compile failed:\n" + error);
                host.MarkModified(codeChanged: true);
                return ToolResult.Text("Compiled successfully.");
            });

        yield return new McpTool("assemble_code",
            "Replaces a code entry's bytecode with the given assembly (as produced by disassemble_code).",
            Schema(("name", Str("Code entry name."), true), ("assembly", Str("Assembly source."), true)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleCode code = data.Code.ByName(Required(a, "name")) ?? throw new ArgumentException($"No code entry named \"{S(a, "name")}\".");
                string error = CodeTools.Assemble(data, code, Required(a, "assembly"), host.RunOnMainThread);
                if (error is not null)
                    return ToolResult.Error("Assembler error:\n" + error);
                host.MarkModified(codeChanged: true);
                return ToolResult.Text("Assembled successfully.");
            });

        yield return new McpTool("search_code",
            "Searches the decompiled GML of all code entries (can take a while on big games).",
            Schema(("query", Str("Text (or regex) to find."), true), ("regex", Bool("Treat query as a regular expression."), false),
                   ("case_sensitive", Bool("Case-sensitive search."), false), ("max_results", Int("Maximum matching lines (default 300)."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                var result = CodeTools.Search(data, Required(a, "query"), B(a, "regex", false), B(a, "case_sensitive", false),
                                              host.DecompileContext, I(a, "max_results") ?? 300);
                StringBuilder sb = new($"{result.Matches.Count} matching line(s) in {result.Searched} code entries.\n");
                foreach (var (code, line, text) in result.Matches)
                    sb.AppendLine($"{code}:{line}: {text}");
                if (result.Failed.Count > 0)
                    sb.AppendLine($"Failed to decompile ({result.Failed.Count}): {string.Join(", ", result.Failed.Take(20))}");
                return ToolResult.Text(sb.ToString());
            }, ReadOnly: true);

        // ---------------- Images ----------------

        var imageTarget = new[]
        {
            ("category", Enum("Kind of image.", "Sprites", "Backgrounds", "Fonts", "TexturePageItems", "EmbeddedTextures"), true),
            nameOrIndex[0], nameOrIndex[1],
            ("frame", Int("Sprite frame (default 0)."), false),
        };

        yield return new McpTool("get_image",
            "Returns an image as PNG so you can look at it: a sprite frame, background, font sheet, texture page item or whole texture page.",
            Schema(imageTarget[0], imageTarget[1], imageTarget[2], imageTarget[3],
                   ("max_size", Int("Downscale so the largest side is at most this (default 1024)."), false)), a =>
            {
                var (label, item, page) = ResolveImage(RequireData(host), a);
                ArgbImage image = page is not null ? ImageCodec.DecodePage(page) : ImageCodec.GetPageItemImage(item);
                int max = I(a, "max_size") ?? 1024;
                string size = $"{image.Width}x{image.Height}";
                if (image.Width > max || image.Height > max)
                {
                    double scale = (double)max / Math.Max(image.Width, image.Height);
                    image = image.Resize(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
                    size += $" (shown at {image.Width}x{image.Height})";
                }
                return new ToolResult().AddText($"{label}: {size}").AddImage(image.ToPng());
            }, ReadOnly: true);

        yield return new McpTool("replace_image",
            "Replaces an image (sprite frame, background, font sheet, texture page item or whole page) with a PNG, keeping the texture page format. The image is scaled to the space the item has on its page.",
            Schema(imageTarget[0], imageTarget[1], imageTarget[2], imageTarget[3],
                   ("png_base64", Str("PNG file content, base64."), false), ("file_path", Str("Or: path of a PNG file on the device."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                var (label, item, page) = ResolveImage(data, a);
                ArgbImage image = LoadPngArgument(a);
                if (page is not null)
                    ImageCodec.ReplacePage(page, image);
                else
                    ImageCodec.ReplacePageItem(item, image);
                host.MarkModified();
                return ToolResult.Text($"Replaced {label} with a {image.Width}x{image.Height} image.");
            });

        yield return new McpTool("add_sprite_frame",
            "Appends a frame to a sprite from a PNG (scaled to the sprite size; stored on a new texture page).",
            Schema(("sprite", Str("Sprite name."), true), ("png_base64", Str("PNG file content, base64."), false),
                   ("file_path", Str("Or: path of a PNG file on the device."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleSprite sprite = data.Sprites.ByName(Required(a, "sprite")) ?? throw new ArgumentException($"No sprite named \"{S(a, "sprite")}\".");
                int frame = AssetTools.AddSpriteFrame(data, sprite, LoadPngArgument(a));
                host.MarkModified();
                return ToolResult.Text($"Added frame {frame} to {sprite.Name.Content}.");
            });

        yield return new McpTool("export_images",
            "Exports images of a category (Sprites: every frame; Backgrounds; Fonts; EmbeddedTextures) as PNG files into a folder on the device.",
            Schema(("category", Enum("What to export.", "Sprites", "Backgrounds", "Fonts", "EmbeddedTextures"), true),
                   ("filter", Str("Only resources whose name contains this."), false),
                   ("directory", Str("Output folder (default: <work dir>/Exported images/<category>)."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                string category = Required(a, "category");
                string filter = S(a, "filter");
                string dir = S(a, "directory") ?? Path.Combine(host.WorkDirectory, "Exported images", category);
                Directory.CreateDirectory(dir);
                TexturePageCache cache = new();
                int count = 0;
                List<string> errors = new();
                void Export(string name, Action action)
                {
                    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        return;
                    try
                    {
                        action();
                        count++;
                    }
                    catch (Exception e)
                    {
                        errors.Add($"{name}: {e.Message}");
                    }
                }
                switch (category)
                {
                    case "Sprites":
                        foreach (var s in data.Sprites.Where(s => s is not null))
                        {
                            for (int f = 0; f < s.Textures.Count; f++)
                            {
                                var tex = s.Textures[f]?.Texture;
                                if (tex is null)
                                    continue;
                                int frame = f;
                                Export(s.Name.Content, () => ImageCodec.ExportPageItemPng(tex, Path.Combine(dir, $"{s.Name.Content}_{frame}.png"), true, cache));
                            }
                        }
                        break;
                    case "Backgrounds":
                        foreach (var b in data.Backgrounds.Where(b => b?.Texture is not null))
                            Export(b.Name.Content, () => ImageCodec.ExportPageItemPng(b.Texture, Path.Combine(dir, b.Name.Content + ".png"), true, cache));
                        break;
                    case "Fonts":
                        foreach (var f in data.Fonts.Where(f => f?.Texture is not null))
                            Export(f.Name.Content, () => ImageCodec.ExportPageItemPng(f.Texture, Path.Combine(dir, f.Name.Content + ".png"), true, cache));
                        break;
                    case "EmbeddedTextures":
                        for (int i = 0; i < data.EmbeddedTextures.Count; i++)
                        {
                            var t = data.EmbeddedTextures[i];
                            int index = i;
                            Export(t.Name?.Content ?? i.ToString(), () => cache.Get(t).SavePng(Path.Combine(dir, $"{index}.png")));
                        }
                        break;
                    default:
                        return ToolResult.Error("Unknown category.");
                }
                return ToolResult.Text($"Exported {count} image(s) to {dir}" + (errors.Count > 0 ? $"\n{errors.Count} error(s):\n" + string.Join("\n", errors.Take(20)) : ""));
            });

        yield return new McpTool("import_images",
            "Imports PNG files from a folder on the device, like UTMT's ImportGraphics: name.png replaces the background or font with that name; sprite_N.png becomes frame N of sprite \"sprite\" (new sprites are created). Images are trimmed and packed onto new texture pages, so sizes may change.",
            Schema(("directory", Str("Folder containing the PNG files."), true), ("recursive", Bool("Include subfolders."), false),
                   ("create_sprites", Bool("Create sprites that don't exist (default true)."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                string dir = Required(a, "directory");
                var files = Directory.GetFiles(dir, "*.png", B(a, "recursive", false) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                var images = files.OrderBy(f => f, StringComparer.Ordinal).Select(f => (Path.GetFileNameWithoutExtension(f), PngDecoder.Decode(f))).ToList();
                var report = AssetTools.ImportImages(data, images, B(a, "create_sprites", true));
                host.MarkModified();
                return ToolResult.Text($"{files.Length} PNG file(s): {report}");
            });

        // ---------------- Rooms ----------------

        yield return new McpTool("get_room",
            "Describes a room: size, layers and all instances (id, object, position, scale, rotation, layer).",
            Schema(("name", Str("Room name."), true)), a =>
            {
                UndertaleRoom room = RequireData(host).Rooms.ByName(Required(a, "name")) ?? throw new ArgumentException($"No room named \"{S(a, "name")}\".");
                StringBuilder sb = new($"{room.Name.Content}: {room.Width}x{room.Height}, speed {room.Speed}, persistent {room.Persistent}\n");
                if (room.Layers is { Count: > 0 })
                {
                    sb.AppendLine("Layers:");
                    foreach (Layer l in room.Layers.OrderBy(l => l.LayerDepth))
                        sb.AppendLine($"  {l.LayerName?.Content} (id {l.LayerId}, {l.LayerType}, depth {l.LayerDepth}, visible {l.IsVisible})");
                }
                sb.AppendLine($"Instances ({room.GameObjects.Count}):");
                foreach (GameObject o in room.GameObjects)
                {
                    string layer = room.Layers?.FirstOrDefault(l => l.InstancesData?.Instances.Contains(o) == true)?.LayerName?.Content;
                    sb.AppendLine($"  #{o.InstanceID} {o.ObjectDefinition?.Name?.Content} at ({o.X}, {o.Y}) scale ({o.ScaleX}, {o.ScaleY}) rot {o.Rotation}" +
                                  (layer is null ? "" : $" layer {layer}") + (o.CreationCode is null ? "" : $" creation code {o.CreationCode.Name?.Content}"));
                }
                if (room.Tiles.Count > 0)
                    sb.AppendLine($"Tiles: {room.Tiles.Count} (use get_resource Rooms path Tiles for details)");
                return ToolResult.Text(sb.ToString());
            }, ReadOnly: true);

        yield return new McpTool("add_instance",
            "Adds an object instance to a room. For rooms with layers, the instance goes to the given instance layer (default: the first one).",
            Schema(("room", Str("Room name."), true), ("object", Str("Object name, e.g. obj_player."), true),
                   ("x", Int("X position."), true), ("y", Int("Y position."), true), ("layer", Str("Instance layer name (GMS2 rooms)."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleRoom room = data.Rooms.ByName(Required(a, "room")) ?? throw new ArgumentException("Room not found.");
                UndertaleGameObject obj = data.GameObjects.ByName(Required(a, "object")) ?? throw new ArgumentException("Object not found.");
                Layer layer = null;
                if (room.Layers is { Count: > 0 })
                {
                    string layerName = S(a, "layer");
                    layer = room.Layers.FirstOrDefault(l => l.InstancesData is not null && (layerName is null || l.LayerName?.Content == layerName))
                        ?? throw new ArgumentException(layerName is null ? "Room has no instance layer." : $"No instance layer named \"{layerName}\".");
                }
                GameObject instance = new()
                {
                    X = I(a, "x")!.Value,
                    Y = I(a, "y")!.Value,
                    ObjectDefinition = obj,
                    InstanceID = data.GeneralInfo.LastObj++,
                };
                host.RunOnMainThread(() =>
                {
                    room.GameObjects.Add(instance);
                    layer?.InstancesData.Instances.Add(instance);
                });
                host.MarkModified();
                return ToolResult.Text($"Added instance #{instance.InstanceID} of {obj.Name.Content} at ({instance.X}, {instance.Y}).");
            });

        yield return new McpTool("update_instance",
            "Changes an instance in a room (position, scale, rotation, object, image index).",
            Schema(("room", Str("Room name."), true), ("instance_id", Int("Instance ID (from get_room)."), true),
                   ("x", Int("New X."), false), ("y", Int("New Y."), false), ("scale_x", Num("New X scale."), false), ("scale_y", Num("New Y scale."), false),
                   ("rotation", Num("New rotation (degrees)."), false), ("object", Str("New object name."), false), ("image_index", Int("New image index."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleRoom room = data.Rooms.ByName(Required(a, "room")) ?? throw new ArgumentException("Room not found.");
                uint id = (uint)I(a, "instance_id")!.Value;
                GameObject o = room.GameObjects.FirstOrDefault(g => g.InstanceID == id) ?? throw new ArgumentException($"No instance #{id} in this room.");
                host.RunOnMainThread(() =>
                {
                    if (I(a, "x") is int x) o.X = x;
                    if (I(a, "y") is int y) o.Y = y;
                    if (F(a, "scale_x") is float sx) o.ScaleX = sx;
                    if (F(a, "scale_y") is float sy) o.ScaleY = sy;
                    if (F(a, "rotation") is float r) o.Rotation = r;
                    if (I(a, "image_index") is int ii) o.ImageIndex = ii;
                    if (S(a, "object") is string objName)
                        o.ObjectDefinition = data.GameObjects.ByName(objName) ?? throw new ArgumentException("Object not found.");
                });
                host.MarkModified();
                return ToolResult.Text($"Instance #{id}: {o.ObjectDefinition?.Name?.Content} at ({o.X}, {o.Y}) scale ({o.ScaleX}, {o.ScaleY}) rot {o.Rotation}");
            });

        yield return new McpTool("delete_instance",
            "Removes an instance from a room.",
            Schema(("room", Str("Room name."), true), ("instance_id", Int("Instance ID."), true)), a =>
            {
                UndertaleRoom room = RequireData(host).Rooms.ByName(Required(a, "room")) ?? throw new ArgumentException("Room not found.");
                uint id = (uint)I(a, "instance_id")!.Value;
                GameObject o = room.GameObjects.FirstOrDefault(g => g.InstanceID == id) ?? throw new ArgumentException($"No instance #{id} in this room.");
                host.RunOnMainThread(() =>
                {
                    if (room.Layers is not null)
                    {
                        foreach (Layer l in room.Layers)
                            l.InstancesData?.Instances.Remove(o);
                    }
                    room.GameObjects.Remove(o);
                });
                host.MarkModified();
                return ToolResult.Text($"Deleted instance #{id}.");
            });

        // ---------------- Sounds ----------------

        yield return new McpTool("export_sound",
            "Saves an embedded sound (WAV/OGG) to a file on the device and returns the path.",
            Schema(("name", Str("Sound name."), true), ("directory", Str("Output folder (default: <work dir>/Exported sounds)."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleSound sound = data.Sounds.ByName(Required(a, "name")) ?? throw new ArgumentException("Sound not found.");
                UndertaleEmbeddedAudio audio = EmbeddedAudioOf(data, sound);
                string dir = S(a, "directory") ?? Path.Combine(host.WorkDirectory, "Exported sounds");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, sound.Name.Content + AssetTools.DetectAudioExtension(audio.Data));
                File.WriteAllBytes(path, audio.Data);
                return ToolResult.Text($"Saved {audio.Data.Length} bytes to {path}");
            });

        yield return new McpTool("replace_sound",
            "Replaces an embedded sound's audio with a WAV or OGG file.",
            Schema(("name", Str("Sound name."), true), ("audio_base64", Str("WAV/OGG content, base64."), false), ("file_path", Str("Or: file path on the device."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleSound sound = data.Sounds.ByName(Required(a, "name")) ?? throw new ArgumentException("Sound not found.");
                AssetTools.ReplaceSoundAudio(data, sound, EmbeddedAudioOf(data, sound), LoadBytesArgument(a, "audio_base64"));
                host.MarkModified();
                return ToolResult.Text($"Replaced audio of {sound.Name.Content}.");
            });

        yield return new McpTool("add_sound",
            "Adds a new sound (embedded WAV or OGG) to the game.",
            Schema(("name", Str("New sound name, e.g. snd_new."), true), ("audio_base64", Str("WAV/OGG content, base64."), false), ("file_path", Str("Or: file path on the device."), false)), a =>
            {
                UndertaleData data = RequireData(host);
                UndertaleSound sound = AssetTools.AddSound(data, Required(a, "name"), LoadBytesArgument(a, "audio_base64"));
                host.MarkModified();
                return ToolResult.Text($"Added sound {sound.Name.Content} (#{data.Sounds.Count - 1}).");
            });

        // ---------------- Scripts ----------------

        var scriptOptions = new[]
        {
            ("question_answer", Bool("Answer to every ScriptQuestion (default true)."), false),
            ("inputs", Prop("array", "Answers for text inputs and file/folder prompts, in order (then defaults are used).", new JsonObject { ["items"] = new JsonObject { ["type"] = "string" } }), false),
        };

        yield return new McpTool("run_csharp",
            "Runs C# code as a UndertaleModTool script (the desktop UTMT scripting API: Data, ScriptMessage, GetDecompiledText, CodeImportGroup, ...; UndertaleModTool.Core.Imaging.ImageCodec for images without ImageMagick). Messages, questions and prompts are logged and answered automatically. The value of the last expression / return value is reported.",
            Schema(("code", Str("C# script code."), true), scriptOptions[0], scriptOptions[1]), a =>
                RunScript(host, Required(a, "code"), null, a));

        yield return new McpTool("list_scripts",
            "Lists the bundled UTMT scripts (and the user's scripts) that run_script can run.",
            Schema(("filter", Str("Only paths containing this."), false)), a =>
            {
                string filter = S(a, "filter");
                StringBuilder sb = new();
                foreach (string root in new[] { host.ScriptsDirectory, Path.Combine(host.WorkDirectory, "Scripts") })
                {
                    if (!Directory.Exists(root))
                        continue;
                    foreach (string file in Directory.GetFiles(root, "*.csx", SearchOption.AllDirectories).OrderBy(f => f))
                    {
                        if (filter is null || file.Contains(filter, StringComparison.OrdinalIgnoreCase))
                            sb.AppendLine(file);
                    }
                }
                return ToolResult.Text(sb.Length == 0 ? "No scripts found." : sb.ToString());
            }, ReadOnly: true);

        yield return new McpTool("run_script",
            "Runs a .csx script file (a path from list_scripts, or any .csx on the device). Folder prompts get a fresh folder under the work directory unless inputs are given.",
            Schema(("path", Str("Script path."), true), scriptOptions[0], scriptOptions[1]), a =>
            {
                string path = Required(a, "path");
                if (!File.Exists(path))
                {
                    string candidate = Path.Combine(host.ScriptsDirectory, path);
                    path = File.Exists(candidate) ? candidate : throw new ArgumentException($"Script not found: {path}");
                }
                return RunScript(host, File.ReadAllText(path, Encoding.UTF8), path, a);
            });
    }

    private static ToolResult RunScript(IUmtHost host, string code, string path, JsonObject a)
    {
        var inputs = (a["inputs"] as JsonArray)?.Select(n => n?.ToString()).ToList();
        HeadlessScriptHost script = new(host, B(a, "question_answer", true), inputs);
        var (success, result) = script.RunAsync(code, path).GetAwaiter().GetResult();
        if (host.Data is not null)
            host.MarkModified(codeChanged: true);
        StringBuilder sb = new();
        sb.AppendLine(success ? "Script finished successfully." : $"Script failed ({script.ScriptErrorType}):\n{script.ScriptErrorMessage}");
        if (result is not null)
            sb.AppendLine("Result: " + result);
        string log = script.Log;
        if (log.Length > 0)
            sb.AppendLine("Output:\n" + (log.Length > 60_000 ? log[..60_000] + "\n...(truncated)" : log));
        if (Directory.Exists(script.OutputDirectory))
            sb.AppendLine("Output folder: " + script.OutputDirectory);
        return ToolResult.Text(sb.ToString(), isError: !success);
    }

    private static UndertaleEmbeddedAudio EmbeddedAudioOf(UndertaleData data, UndertaleSound sound)
    {
        if (sound.GroupID != data.GetBuiltinSoundGroupID())
            throw new InvalidOperationException($"This sound is in audio group {sound.GroupID} (a separate audiogroup file); open it in the app to play or replace it.");
        return sound.AudioFile ?? throw new InvalidOperationException("This sound is not embedded (external file: " + sound.File?.Content + ").");
    }

    private static byte[] LoadBytesArgument(JsonObject a, string base64Key)
    {
        if (S(a, base64Key) is string b64)
            return Convert.FromBase64String(b64.Contains(',') && b64.StartsWith("data:", StringComparison.Ordinal) ? b64[(b64.IndexOf(',') + 1)..] : b64);
        if (S(a, "file_path") is string path)
            return File.ReadAllBytes(path);
        throw new ArgumentException($"Give either \"{base64Key}\" or \"file_path\".");
    }

    private static ArgbImage LoadPngArgument(JsonObject a)
    {
        byte[] bytes = LoadBytesArgument(a, "png_base64");
        if (PngDecoder.IsPng(bytes))
            return PngDecoder.Decode(bytes);
        if (DdsDecoder.IsDds(bytes))
            return DdsDecoder.Decode(bytes);
        throw new ArgumentException("The image must be a PNG file.");
    }

    private static (string Label, UndertaleTexturePageItem Item, UndertaleEmbeddedTexture Page) ResolveImage(UndertaleData data, JsonObject a)
    {
        string category = Required(a, "category");
        IList list = ObjectInspector.GetCategory(data, category);
        object item = ObjectInspector.FindItem(list, S(a, "name"), I(a, "index"), out int index);
        string label = $"{category} #{index} {ObjectInspector.NameOf(item)}";
        switch (item)
        {
            case UndertaleSprite sprite:
                int frame = I(a, "frame") ?? 0;
                if (frame < 0 || frame >= sprite.Textures.Count)
                    throw new ArgumentException($"Frame {frame} out of range (sprite has {sprite.Textures.Count} frames).");
                return ($"{label} frame {frame}", sprite.Textures[frame]?.Texture ?? throw new InvalidOperationException("Frame has no texture."), null);
            case UndertaleBackground background:
                return (label, background.Texture ?? throw new InvalidOperationException("No texture."), null);
            case UndertaleFont font:
                return (label, font.Texture ?? throw new InvalidOperationException("No texture."), null);
            case UndertaleTexturePageItem pageItem:
                return (label, pageItem, null);
            case UndertaleEmbeddedTexture texture:
                return (label, null, texture);
            default:
                throw new ArgumentException("That category has no images.");
        }
    }
}
