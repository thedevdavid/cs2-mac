# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - 2026-04-09

### Fixed
- **Critical regression fix for v1.1.0.** Patch 11 (`LongPath.AddLongPathPrefix` → no-op) now uses a clean `ILProcessor` rebuild instead of NOP-in-place mutation. The v1.1.0 approach left orphaned `ExceptionHandlers`, stale `MaxStackSize`, and dangling branch metadata that Mono's IL verifier rejected with `InvalidProgramException: Invalid IL code in System.IO.LongPath:AddLongPathPrefix (string): IL_0017: nop`. This cascaded into continuous `NullReferenceException` in `GameManager.Update()`, `LateUpdateWorld()`, and `OnGUI()`, making the game unplayable.

### Added
- `--rollback` CLI flag: `dotnet run -- --rollback "/path/to/bottle"` restores every `.backup` file over its patched sibling, strips `WINEDEBUG`/`MONO_GC_PARAMS` lines from `cxbottle.conf`, and restores `UnityCrashHandler64.exe` from `.disabled`. Backup files are preserved so a subsequent normal run can re-patch idempotently.

### Removed
- `MONO_GC_PARAMS=max-heap-size=4096m` env var — it was speculative, not load-bearing, and 4 GB could be too restrictive for heavily modded installs. Default Mono heap behavior is better.

### Known broken
- **v1.1.0 is broken and must be skipped.** If you already applied v1.1.0, run `dotnet run -- --rollback "/path/to/bottle"` before applying v1.1.1.

## [1.1.0] - 2026-04-09 [BROKEN — DO NOT USE]

### Added
- Patch 11: `LongPath.AddLongPathPrefix` in `Colossal.IO.dll` — strips `\\?\` path prefix that Wine's `CreateFileW` and `RemoveDirectoryW` can't handle. Fixes "IOException: Success" on `.coc` settings files for all mods. **The NOP-in-place mutation approach used here produces malformed IL that Mono rejects at module load — see v1.1.1 for the correct fix.**
- `WINEDEBUG=-all` environment variable — suppresses Wine debug I/O during heavy mod loading.
- `MONO_GC_PARAMS=max-heap-size=4096m` environment variable — larger Mono heap for loading many mod assemblies. (Removed in v1.1.1.)

### Fixed
- `.coc` settings file read errors that affected every mod on game quit.
- `LongFile.OpenRead` / `LongFile.GetFileHandle` failures caused by Wine not handling `\\?\` path prefix in `CreateFileW`.

### Broken
- `GameManager.Update/LateUpdateWorld/OnGUI` `NullReferenceException` cascade caused by malformed IL in the NOP-in-place Patch 11 implementation. Game is unplayable after this version. Fixed in v1.1.1.

## [1.0.0] - 2026-04-08

### Added
- Initial release with 10 DLL patches and 4 config changes.
- Patches `Colossal.IO.dll` (FindNextFile error handling), `PDX.SDK.dll` (Win32 long path fixes), `Colossal.IO.AssetDatabase.dll` (.priority File.Exists bug), `Backtrace.Unity.dll` (sharing violation fix).
- Config changes: `boot.config` (disable graphics jobs), `system.reg` (LongPathsEnabled), `cxbottle.conf` (UNITY_DISABLE_GRAPHICS_JOBS), crash handler disabled.
- Idempotent patching with backup/restore.
- Auto-discovery of CS2 installation in CrossOver bottle.
