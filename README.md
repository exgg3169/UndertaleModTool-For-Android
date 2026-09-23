# UndertaleModTool for Android

An Android port of [UndertaleModTool](https://github.com/UnderminersTeam/UndertaleModTool) (UTMT),
the modding tool for Undertale, Deltarune and other GameMaker games.

The desktop tool's UI is WPF (Windows only), so this port keeps UTMT's cross-platform core unchanged:
**UndertaleModLib** (data file reader/writer, compiler, assembler) and **Underanalyzer** (decompiler).
On top of that it adds a new native Android UI (`UndertaleModTool.Android`, .NET for Android).

## Features

| Feature | Status |
|---|---|
| Open `data.win` / `game.unx` / `game.ios` / `game.droid` (system file picker) | ✅ |
| Save / Save as (back to the same document, or a new one) | ✅ |
| Browse every resource category (sprites, rooms, objects, code, strings, ...) with filtering | ✅ |
| Edit any resource field (text, numbers, enums, flags, references, nested lists) | ✅ generic editor |
| Edit strings | ✅ |
| Decompile code to GML, edit and recompile it | ✅ |
| Disassemble code, edit and reassemble it | ✅ |
| Search in all decompiled code (plain text or regex) | ✅ |
| Texture / sprite / background / font previews, export to PNG | ✅ (PNG, QOI, BZ2+QOI and DDS pages) |
| Replace images: sprite frames, backgrounds, fonts, texture page items, whole texture pages | ✅ |
| Add sprite frames, new sprites from images, import a folder of PNGs (like desktop ImportGraphics) | ✅ |
| Room editor: GMS1 backgrounds/tiles and GMS2 layers; move/add/duplicate/delete instances, tiles and asset sprites; paint tile layers | ✅ |
| Play, export, replace and add sounds (WAV/OGG), incl. `audiogroupN.dat` files and external `.ogg` sounds | ✅ |
| DDS textures (preview, export, replace) | ✅ |
| **AI assistant support: MCP server** — Claude Code, Claude Desktop and other MCP clients can edit the game | ✅ |
| Run C# scripts (`.csx`), same scripting API as the desktop tool | ✅ |
| Bundled UTMT scripts (importers, exporters, UTDR scripts, ...) | ✅ (see limitations) |
| Write and run your own scripts / ad-hoc C# code in the app | ✅ |
| Adding/removing room layers, views and backgrounds visually | ❌ (edit existing ones in the property editor; scripts / `run_csharp` can do anything) |

## Download / install

GitHub Actions builds an APK on every push (see the *Build Android APK* workflow and its
`UndertaleModTool-Android` artifact). It runs on Android 7.0 (API 24) and newer, on 64-bit ARM
phones/tablets and x86_64 (emulators, ChromeOS).

## Usage

1. **Open**: pick your game's data file. For Android GameMaker games it's `assets/game.droid` inside the APK
   (extract it with any zip tool); for PC games it's `data.win`.
2. Tap a category to browse it, tap an item to edit it. Code entries open the code editor
   (*Decompiled (GML)* / *Disassembly* tabs, *Compile* in the toolbar).
3. **Scripts**: runs any bundled or custom `.csx` script. Put your own scripts in
   `Android/data/com.underminers.undertalemodtool.android/files/UndertaleModTool/Scripts`, or use
   *Import .csx from device*. Long-press a script to view/edit it.
4. **Images**: open a sprite, background, font, texture page item or embedded texture and use
   *Replace image...* (sprites ask which frame). **Sounds**: *Play*, *Export audio*, *Replace audio...*.
   Sounds stored in an audio group file ask you to open `audiogroupN.dat` from the game folder first;
   replacing such a sound writes that file back directly. External sounds can be played by picking their `.ogg`,
   and *Replace audio* embeds them. The Sprites and Sounds lists have *New sprite from image*, *Import images from
   folder* and *Add sound* in their menu; sprites have *Add frame*. **Rooms**: open a room and tap *Room editor*;
   *Edit mode* switches between instances, tiles & asset sprites, and painting GMS2 tile layers.
5. **Save** writes back to the file you opened (if the storage provider allows it); **Save as** creates a new file.

Scripts that ask for a folder or file get an in-app file browser. It starts in the script working
folder (`Android/data/<package>/files/UndertaleModTool`), which you can reach from a PC over USB.
Files from anywhere else can be brought in with *From device...*. To let scripts use any path on
internal storage, use *menu → Grant full storage access*.

## AI assistants (MCP server)

*Menu → AI assistant (MCP server)* starts a [Model Context Protocol](https://modelcontextprotocol.io) server inside
the app, so an AI assistant can work on the loaded game for you: explore resources, decompile and rewrite code, edit
strings, look at and replace sprites, move things around in rooms, add sounds and run UTMT scripts. It keeps running
in the background (with a notification) while the server is on.

Every request needs the access token shown on that screen. Ways to connect:

* **On the phone itself** (e.g. Claude Code in [Termux](https://termux.dev)):
  ```sh
  claude mcp add --transport http undertalemodtool http://127.0.0.1:8765/mcp --header "Authorization: Bearer <TOKEN>"
  ```
* **From a computer over USB**: run `adb forward tcp:8765 tcp:8765`, then use the same command on the computer.
* **Over Wi-Fi**: enable *Allow network access* in the app and use `http://<phone IP>:8765/mcp` (shown in the app).
* **Clients that only launch local commands** (e.g. Claude Desktop): use
  [`mcp-remote`](https://www.npmjs.com/package/mcp-remote); the app has a *Copy JSON config* button.

The buttons on the screen copy these commands with your token filled in. The server also serves a small **web
console** at `http://127.0.0.1:8765/` where you can try every tool from a browser.

Tools: `get_status`, `open_data_file`, `save_data_file`, `list_files`, `read_file`, `write_file`, `list_resources`,
`get_resource`, `set_resource_property`, `set_string`, `decompile_code`, `disassemble_code`, `import_code`,
`assemble_code`, `search_code`, `get_image`, `replace_image`, `add_sprite_frame`, `export_images`, `import_images`,
`get_room`, `add_instance`, `update_instance`, `delete_instance`, `export_sound`, `replace_sound`, `add_sound`,
`run_csharp`, `list_scripts`, `run_script`. Changes stay in memory until `save_data_file` (or *Save* in the app).

> The token gives full control over the app, including running C# scripts, so only share it with clients you trust
> and leave *Allow network access* off unless you need it.

## Building

Requirements: .NET 10 SDK, the Android workload and an Android SDK (API 36).

```sh
dotnet workload install android
dotnet publish UndertaleModTool.Android -c Release -p:AndroidSdkDirectory=/path/to/android-sdk
# -> UndertaleModTool.Android/bin/Release/net10.0-android/publish/*-Signed.apk
```

The release APK is signed with the debug key by default. Pass the usual `AndroidSigningKeyStore`,
`AndroidSigningKeyAlias`, `AndroidSigningKeyPass` and `AndroidSigningStorePass` properties to sign it
with your own key.

`UndertaleModCli` (upstream's cross-platform command-line tool) is included as well and builds with
`dotnet build UndertaleModCli`.

## How it works

* `UndertaleModLib/`, `Underanalyzer/`, `UndertaleModCli/` and the bundled scripts are taken unmodified
  from upstream UndertaleModTool (commit `f43e12c`, Underanalyzer `4ff50a8`).
* `UndertaleModTool.Core/` holds everything that doesn't need Android, so it's tested on desktop .NET
  (`dotnet run --project UndertaleModTool.Core.Tests`, also run by CI):
  * `Imaging/` — ImageMagick-free image code: PNG encoder and decoder (all color types/bit depths, interlacing),
    DDS decoder (DXT1/3/5, uncompressed), texture page item extraction/replacement. Scripts can use it too
    (`using UndertaleModTool.Core.Imaging;`), see `Scripts/Android Scripts/`.
  * `Assets/` — adding sprite frames and sounds, packing imported images onto texture pages, collision masks.
  * `Scripting/` — the Roslyn script compiler and a headless `IScriptInterface` for scripts run by AI assistants.
  * `Mcp/` — the MCP server: JSON-RPC protocol, Streamable HTTP transport, the tools and the web console.
* `UndertaleModTool.Android/` is the app:
  * `Services/ScriptCompiler.cs` compiles and runs `.csx` scripts with Roslyn. On Android, assemblies live
    inside the APK and have no file path, so `Microsoft.CodeAnalysis.Scripting` can't be used directly.
    Instead, the exact reference assemblies the app is built against are embedded into it at build time
    (see `EmbedScriptReferenceAssemblies` in the `.csproj`), the script is compiled as a Roslyn script
    submission and the result is loaded with `Assembly.Load`. IL trimming and AOT are disabled so scripts
    can use any API.
  * `Services/AndroidScriptHost.cs` implements `IScriptInterface` (the API scripts are written against):
    messages, questions, text input, progress bars, file/folder prompts and search results are shown with
    Android UI.
  * `Services/ImageCodec.cs` replaces textures without ImageMagick: images are decoded by Android, pasted into
    the texture page and re-encoded in the page's original format (its own PNG encoder, or UndertaleModLib's
    managed QOI / BZ2+QOI encoder), mirroring `UndertaleTexturePageItem.ReplaceTexture`.
  * `Ui/RoomView.cs` renders rooms with Android's Canvas, with decoded texture pages cached in the background.
  * `Activities/` has the screens: main screen, resource lists, a generic reflection-based object editor,
    the room editor, the code editor, code search, and the script list, editor and console.

## Limitations

* **ImageMagick is not available on Android** (Magick.NET only ships glibc native libraries), so the desktop
  scripts that use it (`TextureWorker`, e.g. *Resource Exporters/ExportAllSprites*, *Resource Importers/ImportGraphics*)
  throw `DllNotFoundException`. Use the ImageMagick-free versions in **Scripts/Android Scripts** instead
  (ExportAllSprites, ExportAllBackgroundsAndFonts, ExportAllTexturePages, ImportGraphics), or the app's own
  image features. Everything image-related in the app works without ImageMagick.
* *Replace image* scales the image to the space its texture page item has on the page, like the desktop tool.
  To change a sprite's size, use *Import images from folder...* / ImportGraphics, which packs images onto new pages.
* The room editor doesn't draw instance blend colors, and can't add or remove layers, views or backgrounds
  (existing ones can be edited in the property editor; scripts can add them).
* Scripts using WPF / WinForms (`System.Windows.*`) can't run: *ImportGraphicsAdvanced*, *FontEditor*,
  *ExportAllRoomsToPNG*. A few other bundled scripts use APIs that no longer exist in UndertaleModLib and
  fail to compile on the desktop tool too.
* Except for rooms, the desktop tool's specialized editors (sprite, font, object editors, ...) are replaced by
  one generic property editor.
* The data file is copied into app storage when opened, so GameMaker 2022.9+ games with external texture
  pages can't load those textures.
* Large games need a lot of RAM (the whole data file is loaded into memory, as on desktop).

## Credits & license

UndertaleModTool, UndertaleModLib and Underanalyzer are by
[the Underminers team and contributors](https://github.com/UnderminersTeam/UndertaleModTool/graphs/contributors).
This port is licensed under GPLv3, like the original (see `LICENSE.txt`; Underanalyzer's license is in
`Underanalyzer/LICENSE`).
