using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using UndertaleModLib;
using UndertaleModLib.Compiler;
using UndertaleModLib.Models;
using UndertaleModTool.Core.Assets;
using UndertaleModTool.Core.Imaging;
using UndertaleModTool.Core.Mcp;

namespace UndertaleModTool.Core.Tests;

/// <summary>
/// Runs the MCP server over real HTTP and calls every tool like an MCP client would, then saves,
/// reloads and checks that the changes persisted.
/// </summary>
public static class McpTests
{
    private const string Token = "test-token";
    private static readonly HttpClient Http = new();
    private static string _url;
    private static int _id = 1;

    private static async Task<JsonNode> Rpc(string method, JsonObject parameters = null, string token = Token)
    {
        HttpRequestMessage request = new(HttpMethod.Post, _url)
        {
            Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = _id++, ["method"] = method, ["params"] = parameters ?? new JsonObject() }.ToJsonString(),
                                        Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response = await Http.SendAsync(request);
        if ((int)response.StatusCode != 200)
            return new JsonObject { ["http"] = (int)response.StatusCode };
        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<(string Text, bool Error, JsonArray Content)> Call(string tool, JsonObject args)
    {
        JsonNode r = await Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args });
        JsonArray content = r["result"]?["content"] as JsonArray;
        string text = content is null
            ? r.ToJsonString()
            : string.Join("\n", content.Where(c => c["type"]?.GetValue<string>() == "text").Select(c => c["text"]!.GetValue<string>()));
        return (text, r["result"]?["isError"]?.GetValue<bool>() == true || r["error"] is not null, content);
    }

    private static string Short(string text) => text.Replace("\n", " | ")[..Math.Min(160, text.Length)];

    private static byte[] Wav()
    {
        using MemoryStream ms = new();
        using BinaryWriter w = new(ms);
        const int samples = 800;
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(16000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++)
            w.Write((short)(Math.Sin(i / 5.0) * 8000));
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A small game: a sprite, an object with code, a room and a sound.</summary>
    private static (UndertaleData Data, ArgbImage SpriteImage) CreateTestData()
    {
        UndertaleData data = UndertaleData.CreateNew();
        ArgbImage image = new(16, 16);
        for (int i = 0; i < image.Pixels.Length; i++)
            image.Pixels[i] = unchecked((int)0xFFFF0000) | (i << 8);
        UndertaleSprite sprite = new() { Name = data.Strings.MakeString("spr_test"), Width = 16, Height = 16 };
        sprite.Textures.Add(new UndertaleSprite.TextureEntry { Texture = AssetTools.CreatePageWithItem(data, image) });
        data.Sprites.Add(sprite);
        data.GameObjects.Add(new UndertaleGameObject { Name = data.Strings.MakeString("obj_test"), Sprite = sprite });
        data.Rooms.Add(new UndertaleRoom { Name = data.Strings.MakeString("room_test"), Caption = data.Strings.MakeString("") });
        CodeImportGroup group = new(data);
        group.QueueReplace("gml_Object_obj_test_Create_0", "hp = 10; name = \"hello world\";");
        group.Import();
        AssetTools.AddSound(data, "snd_test", Wav());
        return (data, image);
    }

    public static async Task Run(TestContext t)
    {
        TestHost host = new();
        var (data, spriteImage) = CreateTestData();
        host.FilePath = Path.Combine(host.WorkDirectory, "test.win");
        using (FileStream fs = File.Create(host.FilePath))
            UndertaleIO.Write(fs, data);
        host.LoadDataFile(host.FilePath);

        McpServer mcp = UmtTools.Create(host);
        using McpHttpServer server = new(mcp) { Token = Token, HomePage = WebConsole.Html };
        server.Start(0, allowRemote: false);
        _url = $"http://127.0.0.1:{server.Port}/mcp";
        UndertaleRoom Room() => host.Data.Rooms.ByName("room_test");

        // Protocol
        t.Check((await Rpc("ping", null, "wrong"))["http"]?.GetValue<int>() == 401, "wrong token is rejected (401)");
        JsonNode init = await Rpc("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "1" },
        });
        t.Check(init["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18", "initialize");
        int toolCount = ((JsonArray)(await Rpc("tools/list"))["result"]!["tools"]!).Count;
        t.Check(toolCount >= 29, $"tools/list ({toolCount} tools)");
        t.Check((await Http.GetStringAsync($"http://127.0.0.1:{server.Port}/")).Contains("UndertaleModTool MCP"), "web console page");
        HttpRequestMessage notification = new(HttpMethod.Post, _url)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Encoding.UTF8, "application/json"),
        };
        notification.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        t.Check((int)(await Http.SendAsync(notification)).StatusCode == 202, "notification -> 202");
        t.Check((await Rpc("tools/call", new JsonObject { ["name"] = "nope" }))["error"]?["code"]?.GetValue<int>() == -32602, "unknown tool -> -32602");

        // Status and resources
        var (text, error, content) = await Call("get_status", new());
        t.Check(!error && text.Contains("\"Sprites\": 1"), "get_status");
        (text, error, _) = await Call("list_resources", new() { ["category"] = "code" });
        t.Check(!error && text.Contains("gml_Object_obj_test_Create_0"), "list_resources");
        (text, error, _) = await Call("get_resource", new() { ["category"] = "GameObjects", ["name"] = "obj_test" });
        t.Check(!error && text.Contains("spr_test"), "get_resource");
        (text, error, _) = await Call("set_resource_property", new() { ["category"] = "Rooms", ["name"] = "room_test", ["path"] = "Width", ["value"] = 640 });
        t.Check(!error && Room().Width == 640, "set_resource_property (number)");
        (text, error, _) = await Call("set_resource_property", new() { ["category"] = "Sprites", ["name"] = "spr_test", ["path"] = "Textures[0].Texture.TargetX", ["value"] = 0 });
        t.Check(!error, "set_resource_property (nested path)");
        (text, error, _) = await Call("set_resource_property", new() { ["category"] = "GameObjects", ["name"] = "obj_test", ["path"] = "Sprite", ["value"] = null });
        t.Check(!error && host.Data.GameObjects.ByName("obj_test").Sprite is null, "set_resource_property (clear reference)");
        (text, error, _) = await Call("set_resource_property", new() { ["category"] = "GameObjects", ["name"] = "obj_test", ["path"] = "Sprite", ["value"] = "spr_test" });
        t.Check(!error && host.Data.GameObjects.ByName("obj_test").Sprite == host.Data.Sprites.ByName("spr_test"), "set_resource_property (reference by name)");
        (text, error, _) = await Call("set_string", new() { ["index"] = 0, ["value"] = host.Data.Strings[0].Content });
        t.Check(!error, "set_string");

        // Code
        (text, error, _) = await Call("decompile_code", new() { ["name"] = "gml_Object_obj_test_Create_0" });
        t.Check(!error && text.Contains("hello world"), "decompile_code");
        (text, error, _) = await Call("import_code", new() { ["name"] = "gml_Object_obj_test_Create_0", ["mode"] = "append", ["gml"] = "speed = 3;" });
        t.Check(!error, "import_code append");
        (text, error, _) = await Call("import_code", new() { ["name"] = "gml_Object_obj_test_Create_0", ["mode"] = "find_replace", ["search"] = "hello world", ["gml"] = "merhaba dunya" });
        t.Check(!error, "import_code find_replace");
        (text, error, _) = await Call("import_code", new() { ["name"] = "gml_Object_obj_test_Create_0", ["gml"] = "x = = 1;" });
        t.Check(error && text.Contains("Compile failed"), "import_code reports compile errors");
        (text, error, _) = await Call("search_code", new() { ["query"] = "merhaba" });
        t.Check(!error && text.Contains("gml_Object_obj_test_Create_0:2"), "search_code");
        (text, error, _) = await Call("disassemble_code", new() { ["name"] = "gml_Object_obj_test_Create_0" });
        t.Check(!error && text.Contains("pushi"), "disassemble_code");

        // Images
        (_, error, content) = await Call("get_image", new() { ["category"] = "Sprites", ["name"] = "spr_test" });
        JsonNode image = content?.FirstOrDefault(c => c["type"]?.GetValue<string>() == "image");
        t.Check(!error && image is not null && PngDecoder.Decode(Convert.FromBase64String(image["data"]!.GetValue<string>())).Pixels.SequenceEqual(spriteImage.Pixels),
                "get_image returns the exact sprite");
        ArgbImage green = new(16, 16);
        Array.Fill(green.Pixels, unchecked((int)0xFF00FF00));
        (text, error, _) = await Call("replace_image", new() { ["category"] = "Sprites", ["name"] = "spr_test", ["png_base64"] = Convert.ToBase64String(green.ToPng()) });
        t.Check(!error && ImageCodec.GetPageItemImage(host.Data.Sprites.ByName("spr_test").Textures[0].Texture).Pixels.SequenceEqual(green.Pixels), "replace_image");
        (text, error, _) = await Call("add_sprite_frame", new() { ["sprite"] = "spr_test", ["png_base64"] = Convert.ToBase64String(spriteImage.ToPng()) });
        t.Check(!error && host.Data.Sprites.ByName("spr_test").Textures.Count == 2, "add_sprite_frame");
        (text, error, _) = await Call("export_images", new() { ["category"] = "Sprites" });
        t.Check(!error && File.Exists(Path.Combine(host.WorkDirectory, "Exported images", "Sprites", "spr_test_1.png")), "export_images");

        // Rooms
        (text, error, _) = await Call("add_instance", new() { ["room"] = "room_test", ["object"] = "obj_test", ["x"] = 32, ["y"] = 48 });
        t.Check(!error && Room().GameObjects.Count == 1, "add_instance");
        uint instanceId = Room().GameObjects[0].InstanceID;
        (text, error, _) = await Call("update_instance", new() { ["room"] = "room_test", ["instance_id"] = instanceId, ["x"] = 100, ["scale_x"] = 2 });
        t.Check(!error && Room().GameObjects[0].X == 100 && Room().GameObjects[0].ScaleX == 2, "update_instance");
        await Call("add_instance", new() { ["room"] = "room_test", ["object"] = "obj_test", ["x"] = 1, ["y"] = 2 });
        (text, error, _) = await Call("get_room", new() { ["name"] = "room_test" });
        t.Check(!error && text.Contains("Instances (2)"), "get_room");
        (text, error, _) = await Call("delete_instance", new() { ["room"] = "room_test", ["instance_id"] = instanceId });
        t.Check(!error && Room().GameObjects.Count == 1, "delete_instance");

        // Sounds
        (text, error, _) = await Call("export_sound", new() { ["name"] = "snd_test" });
        t.Check(!error && text.Contains("snd_test.wav"), "export_sound");
        (text, error, _) = await Call("add_sound", new() { ["name"] = "snd_new", ["audio_base64"] = Convert.ToBase64String(Wav()) });
        t.Check(!error && host.Data.Sounds.Count == 2, "add_sound");
        (text, error, _) = await Call("replace_sound", new() { ["name"] = "snd_new", ["audio_base64"] = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }) });
        t.Check(error, "replace_sound rejects non-audio data");

        // Scripts
        (text, error, _) = await Call("run_csharp", new()
        {
            ["code"] = "EnsureDataLoaded(); ScriptMessage(\"sprites: \" + Data.Sprites.Count); " +
                       "if (ScriptQuestion(\"ok?\")) { var g = new CodeImportGroup(Data); g.QueueReplace(\"gml_Script_scr_mcp\", \"return 42;\"); g.Import(); } " +
                       "return Data.Code.Count;",
        });
        t.Check(!error && text.Contains("sprites: 1") && text.Contains("-> Yes") && text.Contains("Result: 2"), "run_csharp: " + Short(text));
        (text, error, _) = await Call("run_csharp", new() { ["code"] = "int x = \"no\";" });
        t.Check(error && text.Contains("CS0029"), "run_csharp reports compile errors");
        (text, error, _) = await Call("list_scripts", new() { ["filter"] = "ExportAllStrings" });
        t.Check(!error && text.Contains("ExportAllStrings.csx"), "list_scripts");
        (text, error, _) = await Call("run_script", new() { ["path"] = text.Split('\n')[0].Trim() });
        t.Check(!error && text.Contains("Output folder"), "run_script (bundled ExportAllStrings)");
        (text, error, _) = await Call("list_files", new());
        t.Check(!error && text.Contains("Script output"), "list_files");

        // Android image scripts (no ImageMagick)
        string androidScripts = Path.Combine(host.ScriptsDirectory, "Android Scripts");
        string exportDir = Path.Combine(host.WorkDirectory, "export");
        (text, error, _) = await Call("run_script", new() { ["path"] = Path.Combine(androidScripts, "ExportAllSprites.csx"), ["inputs"] = new JsonArray(exportDir), ["question_answer"] = false });
        t.Check(!error && File.Exists(Path.Combine(exportDir, "spr_test_0.png")) && File.Exists(Path.Combine(exportDir, "spr_test_1.png")), "Android ExportAllSprites: " + Short(text));
        t.Check(File.Exists(Path.Combine(exportDir, "spr_test_1.png")) && PngDecoder.Decode(Path.Combine(exportDir, "spr_test_1.png")).Pixels.SequenceEqual(spriteImage.Pixels),
                "exported sprite frame is exact");

        string importDir = Path.Combine(host.WorkDirectory, "import");
        Directory.CreateDirectory(importDir);
        ArgbImage blue = new(24, 20);
        for (int y = 2; y < 18; y++)
            for (int x = 3; x < 21; x++)
                blue.Pixels[y * 24 + x] = unchecked((int)0xFF0000FF);
        blue.SavePng(Path.Combine(importDir, "spr_test_0.png"));
        spriteImage.SavePng(Path.Combine(importDir, "spr_brand_new_0.png"));
        spriteImage.SavePng(Path.Combine(importDir, "spr_brand_new_1.png"));
        (text, error, _) = await Call("run_script", new() { ["path"] = Path.Combine(androidScripts, "ImportGraphics.csx"), ["inputs"] = new JsonArray(importDir), ["question_answer"] = false });
        UndertaleSprite testSprite = host.Data.Sprites.ByName("spr_test"), newSprite = host.Data.Sprites.ByName("spr_brand_new");
        t.Check(!error && testSprite.Width == 24 && testSprite.Height == 20 && newSprite?.Textures.Count == 2, "Android ImportGraphics: " + Short(text));
        t.Check(ImageCodec.GetPageItemImage(testSprite.Textures[0].Texture).Pixels.SequenceEqual(blue.Pixels) &&
                testSprite.Textures[0].Texture.TargetX == 3 && testSprite.Textures[0].Texture.TargetY == 2,
                "imported frame is trimmed and restores exactly");
        t.Check(newSprite is not null && ImageCodec.GetPageItemImage(newSprite.Textures[1].Texture).Pixels.SequenceEqual(spriteImage.Pixels) &&
                newSprite.CollisionMasks.Count == 1, "new sprite gets frames and a collision mask");
        (text, error, _) = await Call("import_images", new() { ["directory"] = importDir });
        t.Check(!error && text.Contains("3 PNG"), "import_images");
        string pagesDir = Path.Combine(host.WorkDirectory, "pages");
        (text, error, _) = await Call("run_script", new() { ["path"] = Path.Combine(androidScripts, "ExportAllTexturePages.csx"), ["inputs"] = new JsonArray(pagesDir) });
        t.Check(!error && Directory.GetFiles(pagesDir, "*.png").Length == host.Data.EmbeddedTextures.Count, "Android ExportAllTexturePages");
        (text, error, _) = await Call("run_script", new() { ["path"] = Path.Combine(androidScripts, "ExportAllBackgroundsAndFonts.csx"), ["inputs"] = new JsonArray(pagesDir) });
        t.Check(!error, "Android ExportAllBackgroundsAndFonts");

        // Save, reload and check everything persisted
        string saved = Path.Combine(host.WorkDirectory, "saved.win");
        (text, error, _) = await Call("save_data_file", new() { ["path"] = saved });
        t.Check(!error && File.Exists(saved), "save_data_file");
        (text, error, _) = await Call("open_data_file", new() { ["path"] = saved });
        t.Check(!error, "open_data_file");
        (text, _, _) = await Call("decompile_code", new() { ["name"] = "gml_Object_obj_test_Create_0" });
        t.Check(text.Contains("merhaba dunya") && text.Contains("speed = 3"), "code changes persisted");
        t.Check(Room().Width == 640 && Room().GameObjects.Count == 1 && host.Data.Sounds.Count == 2 &&
                host.Data.Code.ByName("gml_Script_scr_mcp") is not null, "room, sound and script changes persisted");
        t.Check(ImageCodec.GetPageItemImage(host.Data.Sprites.ByName("spr_test").Textures[0].Texture).Pixels.SequenceEqual(blue.Pixels) &&
                host.Data.Sprites.ByName("spr_brand_new")?.Textures.Count == 2, "imported graphics persisted");

        server.Stop();
        try
        {
            Directory.Delete(host.WorkDirectory, true);
        }
        catch
        {
            // Best effort
        }
    }
}
