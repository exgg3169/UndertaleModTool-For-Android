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
| Texture / sprite / background / font previews, export to PNG | ✅ (PNG, QOI and BZ2+QOI pages) |
| Run C# scripts (`.csx`), same scripting API as the desktop tool | ✅ |
| Bundled UTMT scripts (importers, exporters, UTDR scripts, ...) | ✅ (see limitations) |
| Write and run your own scripts / ad-hoc C# code in the app | ✅ |
| Image import (replacing sprites/textures), room editor, sound playback | ❌ not yet |

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
4. **Save** writes back to the file you opened (if the storage provider allows it); **Save as** creates a new file.

Scripts that ask for a folder or file get an in-app file browser. It starts in the script working
folder (`Android/data/<package>/files/UndertaleModTool`), which you can reach from a PC over USB.
Files from anywhere else can be brought in with *From device...*. To let scripts use any path on
internal storage, use *menu → Grant full storage access*.

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
* `UndertaleModTool.Android/` is the new app:
  * `Services/ScriptCompiler.cs` compiles and runs `.csx` scripts with Roslyn. On Android, assemblies live
    inside the APK and have no file path, so `Microsoft.CodeAnalysis.Scripting` can't be used directly.
    Instead, the exact reference assemblies the app is built against are embedded into it at build time
    (see `EmbedScriptReferenceAssemblies` in the `.csproj`), the script is compiled as a Roslyn script
    submission and the result is loaded with `Assembly.Load`. IL trimming and AOT are disabled so scripts
    can use any API.
  * `Services/AndroidScriptHost.cs` implements `IScriptInterface` (the API scripts are written against):
    messages, questions, text input, progress bars, file/folder prompts and search results are shown with
    Android UI.
  * `Activities/` has the screens: main screen, resource lists, a generic reflection-based object editor,
    the code editor, code search, and the script list, editor and console.

## Limitations

* **ImageMagick is not available on Android** (Magick.NET only ships glibc native libraries). Anything
  that needs it throws `DllNotFoundException`, which mainly means scripts that export/import images through
  `TextureWorker` (e.g. *ExportAllSprites*, *ImportGraphics*). Previews in the app don't need ImageMagick,
  but DDS textures can't be previewed.
* Scripts using WPF / WinForms (`System.Windows.*`) can't run: *ImportGraphicsAdvanced*, *FontEditor*,
  *ExportAllRoomsToPNG*. A few other bundled scripts use APIs that no longer exist in UndertaleModLib and
  fail to compile on the desktop tool too.
* The desktop tool's specialized editors (room editor, sprite editor, ...) are replaced by one generic
  property editor.
* The data file is copied into app storage when opened, so GameMaker 2022.9+ games with external texture
  pages can't load those textures.
* Large games need a lot of RAM (the whole data file is loaded into memory, as on desktop).

## Credits & license

UndertaleModTool, UndertaleModLib and Underanalyzer are by
[the Underminers team and contributors](https://github.com/UnderminersTeam/UndertaleModTool/graphs/contributors).
This port is licensed under GPLv3, like the original (see `LICENSE.txt`; Underanalyzer's license is in
`Underanalyzer/LICENSE`).
