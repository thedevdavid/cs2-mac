# Cities: Skylines II - CrossOver macOS Patcher

> **v1.1.0** | Compatible with CS2 **v1.5.6f1** (Bridges & Ports) | CrossOver **26.0+**

Run Cities: Skylines II on macOS using CrossOver. This patcher fixes Wine/CrossOver compatibility issues by patching game DLLs and configuring the Wine bottle.

## Prerequisites

| Requirement | Install |
|-------------|---------|
| **macOS** (Apple Silicon or Intel) | — |
| **CrossOver 26+** | [Download from codeweavers.com](https://www.codeweavers.com/crossover) |
| **.NET SDK 9.0+** | `brew install dotnet-sdk` or [download from dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| **Steam** | Install inside your CrossOver bottle via the CrossOver UI |
| **Cities: Skylines II** | Purchase and install via Steam inside the bottle |

## Bottle Setup

1. Create a new bottle in CrossOver:
   - **Windows version**: Windows 10 64-bit
   - **Graphics**: D3DMetal (default in CrossOver 26)
   - **MSync**: Enabled (default in CrossOver 26)
2. Install Steam in the bottle
3. Install Cities: Skylines II via Steam
4. Close the game

## Usage

Download the [latest release](../../releases/latest) or clone the repo, then run:

```bash
dotnet run --project /path/to/cs2-mac -- "$HOME/Library/Application Support/CrossOver/Bottles/YOUR_BOTTLE_NAME"
```

Example with default Steam bottle:
```bash
dotnet run --project . -- "$HOME/Library/Application Support/CrossOver/Bottles/Steam"
```

The patcher is **idempotent** — safe to run multiple times. It restores from backups before patching.

## After Game Updates

Steam updates overwrite patched DLLs and `boot.config`. **Re-run the patcher after every CS2 update.** The patcher detects existing backups and re-patches cleanly.

## Compatibility

| CS2 Version | Patcher Version | Status |
|-------------|-----------------|--------|
| v1.5.6f1 (Bridges & Ports) | v1.1.0 | Tested and working |
| v1.5.6f1 (Bridges & Ports) | v1.0.0 | Tested and working |

Future CS2 updates may shift IL offsets in patched DLLs. If the patcher reports warnings after an update, a new patcher version may be needed.

## What It Patches

### DLL Patches (4 DLLs, 11 patches)

| DLL | Patches | Issue |
|-----|---------|-------|
| `Colossal.IO.dll` | 3 | Wine's `FindNextFile` returns wrong error code; `LongPath.AddLongPathPrefix` prepends `\\?\` to paths which Wine's `CreateFileW`/`RemoveDirectory` can't handle |
| `PDX.SDK.dll` | 7 | Wine's filesystem APIs broken: directory listing, deletion, creation, file I/O |
| `Colossal.IO.AssetDatabase.dll` | 1 | Wine's `File.Exists` returns true for non-existent `.priority` file |
| `Backtrace.Unity.dll` | 1 | Wine file locking causes sharing violations in crash reporter |

### PDX.SDK.dll Details

The Paradox SDK has two code paths for filesystem operations: standard .NET (`System.IO.*`) and Win32 P/Invoke (`kernel32.dll`). Under Wine, the standard .NET path is broken (`IOException: Success`). The patcher:

1. Forces directory operations through Win32 APIs (which we patch for Wine compatibility)
2. Keeps file I/O on standard .NET `FileStream` (Wine's `CreateFileW` with `\\?\` prefix fails for new files)

### Config Changes

| Config | Change | Why |
|--------|--------|-----|
| `boot.config` | `gfx-enable-gfx-jobs=0` | Reduces D3DMetal threading pressure |
| `system.reg` | `LongPathsEnabled=1` | Helps Wine's `\\?\` path handling |
| `cxbottle.conf` | `UNITY_DISABLE_GRAPHICS_JOBS=1` | Additional D3DMetal stability |
| `cxbottle.conf` | `WINEDEBUG=-all` | Suppress Wine debug I/O during heavy mod loading |
| `cxbottle.conf` | `MONO_GC_PARAMS=max-heap-size=4096m` | Larger Mono heap for loading many mod assemblies |
| `UnityCrashHandler64.exe` | Renamed to `.disabled` | Prevents hung-crash under Wine |

## Known Limitations

- **Performance**: Expect lower FPS than Windows due to D3DMetal translation + Rosetta 2 (Apple Silicon)
- **Mods**: Paradox Mods download and work; mods that call `File.Delete` directly may fail under Wine with `IOException` (same Wine bug the patcher fixes in game DLLs, but in mod code we can't patch). Disable problematic mods if they cause crashes during playset loading.
- **Quit**: Game may need force-quit occasionally
- **Editor.coc**: Settings file read error on startup (cosmetic, non-blocking)

## Technical Details

All issues stem from Wine's incomplete Win32 filesystem API implementation:
- `FindNextFile` returns unexpected error codes instead of `ERROR_NO_MORE_FILES`
- `RemoveDirectory`/`DeleteFileW`/`CreateDirectory`/`MoveFileW` fail with `\\?\` path prefix
- `System.IO.Directory.GetDirectories()` throws `IOException: Success` (Wine `CreateDirectoryHandle` bug)
- `File.Exists` returns true for non-existent files
- `CreateFileW` with `\\?\` prefix returns `INVALID_HANDLE_VALUE` for file creation operations ([Wine Bug #44730](https://bugs.winehq.org/show_bug.cgi?id=44730))
- `File.Delete` throws `IOException: Success` (Wine returns errno 0 but Mono interprets as error)

## License

MIT
