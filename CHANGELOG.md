# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-04-09

### Added
- Patch 11: `LongPath.AddLongPathPrefix` in `Colossal.IO.dll` — strips `\\?\` path prefix that Wine's `CreateFileW` and `RemoveDirectoryW` can't handle. Fixes "IOException: Success" on `.coc` settings files for all mods.
- `WINEDEBUG=-all` environment variable — suppresses Wine debug I/O during heavy mod loading.
- `MONO_GC_PARAMS=max-heap-size=4096m` environment variable — larger Mono heap for loading many mod assemblies.

### Fixed
- `.coc` settings file read errors that affected every mod on game quit.
- `LongFile.OpenRead` / `LongFile.GetFileHandle` failures caused by Wine not handling `\\?\` path prefix in `CreateFileW`.

## [1.0.0] - 2026-04-08

### Added
- Initial release with 10 DLL patches and 4 config changes.
- Patches `Colossal.IO.dll` (FindNextFile error handling), `PDX.SDK.dll` (Win32 long path fixes), `Colossal.IO.AssetDatabase.dll` (.priority File.Exists bug), `Backtrace.Unity.dll` (sharing violation fix).
- Config changes: `boot.config` (disable graphics jobs), `system.reg` (LongPathsEnabled), `cxbottle.conf` (UNITY_DISABLE_GRAPHICS_JOBS), crash handler disabled.
- Idempotent patching with backup/restore.
- Auto-discovery of CS2 installation in CrossOver bottle.
