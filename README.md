# Prism

Prism is an Android Unreal Engine `.pak` browser and extractor built on top of CUE4Parse.

It is designed for quick on-device inspection of UE4/UE5 package archives: open a pak, browse it like a file manager, preview supported texture assets, and export raw package files or PNG images.

## Features

- Android-first WebView UI with portrait and landscape layouts.
- UE `.pak` mounting through CUE4Parse.
- Known AES key support for encrypted paks.
- Optional `.usmap` import for unversioned UE asset properties.
- File-manager style package browsing and search.
- Groups related `.uasset`, `.uexp`, and `.ubulk` files as one visible asset.
- Raw export of grouped package files.
- PNG export for supported `UTexture` assets.
- Image preview with pan and zoom.
- In-app diagnostics panel for decoding and native library troubleshooting.
- Optional Android Oodle native loading for Oodle-compressed paks.

## Current Scope

Prism currently focuses on `.pak` archives. `.utoc` and `.ucas` containers are not implemented yet.

Mapping files are not bundled by default. Use the **Mapping** picker inside the app to import a matching `.usmap` when a game uses unversioned properties.

## Oodle Support

Oodle is optional and is not committed to this repository.

If you have a legally usable Android Oodle build, place it here before building:

```text
third_party/lib/arm64-v8a/liboodle-data-shared.so
```

The library must be an Android `arm64-v8a` shared object exporting:

```text
OodleLZ_Decompress
OodleLZ_Compress
OodleLZ_GetCompressedBufferSizeNeeded
```

It should depend only on Android system libraries such as `libc.so`, `libm.so`, `libdl.so`, and `liblog.so`. Linux/glibc builds that depend on `libc.so.6`, `libstdc++.so.6`, or `ld-linux-aarch64.so.1` will not load on Android.

If no Oodle library is present, Prism still builds and works for uncompressed/zlib-compatible paks, but Oodle-compressed entries cannot be decoded.

### Oodle / Unreal EULA Notice

Prism does not claim ownership of Oodle, Unreal Engine code, game assets, mapping files, AES keys, or any native build artifact derived from Epic Games, RAD Game Tools, or a third-party game project.

If you provide an Oodle native library for local builds, you are responsible for making sure your use and distribution of that library complies with the Unreal Engine EULA, RAD/Epic licensing terms, and any applicable game or platform agreements. This repository is intended to make Prism easier to build and test; it is not a grant of rights to redistribute proprietary middleware or game content.

If Epic Games, RAD Game Tools, or another rights holder believes any Prism documentation or build integration should be changed, please contact the project maintainer so the repository can be adjusted or the affected material can be removed.

## Build

Install the .NET Android workload and Android SDK/NDK, then build:

```powershell
dotnet workload restore
dotnet build Prism\Prism.csproj
```

Release APK:

```powershell
dotnet publish Prism\Prism.csproj -c Release -f net10.0-android -p:AndroidPackageFormat=apk
```

Debug install example:

```powershell
& "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe" install --no-incremental -r "Prism\bin\Debug\net10.0-android\com.ccbteam.prism-Signed.apk"
```

## Repository Layout

- `Prism` - Android app and WebView UI.
- `PakTool.Core` - Pak mounting, browsing, export, and texture preview logic.
- `PakTool.Cli` - Desktop diagnostic harness for quick parser checks.
- `CUE4Parse` - Unreal archive and asset parser dependency.

## Notes

Use Prism only with archives and keys you are authorized to access. Prism does not include game content, AES keys, mapping files, or proprietary Oodle redistributables.

## License

This project is based on FModel/CUE4Parse code and follows the repository license terms. See `LICENSE` and `NOTICE`.
