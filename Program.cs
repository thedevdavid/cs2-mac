using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;


/// <summary>
/// CS2 CrossOver macOS Patcher
/// Patches Cities: Skylines II DLLs and config to fix Wine/CrossOver compatibility issues.
/// All issues stem from Wine's incomplete Win32 filesystem API implementation.
///
/// Usage:
///   dotnet run -- "/path/to/CrossOver/Bottles/BottleName"
///   dotnet run -- --rollback "/path/to/CrossOver/Bottles/BottleName"
/// </summary>

var isRollback = args.Length >= 1 && args[0] == "--rollback";
var bottlePath = isRollback
    ? (args.Length >= 2 ? args[1] : "")
    : (args.Length > 0 ? args[0] : "");

if (string.IsNullOrEmpty(bottlePath) || !Directory.Exists(bottlePath))
{
    Console.Error.WriteLine("CS2 CrossOver Patcher");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run -- \"/path/to/CrossOver/Bottles/Steam\"");
    Console.Error.WriteLine("  dotnet run -- --rollback \"/path/to/CrossOver/Bottles/Steam\"");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  e.g. dotnet run -- \"$HOME/Library/Application Support/CrossOver/Bottles/Steam\"");
    Environment.Exit(1);
}

// Auto-discover paths
var driveC = Path.Combine(bottlePath, "drive_c");
var gamePath = Path.Combine(driveC, "Program Files (x86)", "Steam", "steamapps", "common", "Cities Skylines II");
var managedPath = Path.Combine(gamePath, "Cities2_Data", "Managed");
var bootConfigPath = Path.Combine(gamePath, "Cities2_Data", "boot.config");
var crashHandlerPath = Path.Combine(gamePath, "UnityCrashHandler64.exe");
var bottleConfPath = Path.Combine(bottlePath, "cxbottle.conf");
var systemRegPath = Path.Combine(bottlePath, "system.reg");

if (!Directory.Exists(managedPath))
{
    Console.Error.WriteLine($"ERROR: CS2 not found at {gamePath}");
    Console.Error.WriteLine("Make sure Cities: Skylines II is installed in this bottle.");
    Environment.Exit(1);
}

if (isRollback)
{
    RollbackAll();
    return;
}

Console.WriteLine("=== CS2 CrossOver macOS Patcher ===");
Console.WriteLine($"Bottle: {bottlePath}");
Console.WriteLine($"Game:   {gamePath}");
Console.WriteLine();

int totalPatches = 0;

// ============================================================
// SECTION 1: DLL Patches
// ============================================================

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managedPath);
var readerParams = new ReaderParameters { AssemblyResolver = resolver, InMemory = true };

// --- Patches 1, 11: Colossal.IO.dll (FindNextFile error handling + LongPath prefix) ---
totalPatches += PatchColossalIO(Path.Combine(managedPath, "Colossal.IO.dll"), readerParams);

// --- Patches 2-8: PDX.SDK.dll (Win32 long path fixes + IsLongPath + CreateFileStream) ---
totalPatches += PatchPdxSdk(Path.Combine(managedPath, "PDX.SDK.dll"), readerParams);

// --- Patch 9: Colossal.IO.AssetDatabase.dll (.priority File.Exists bug) ---
totalPatches += PatchAssetDatabase(Path.Combine(managedPath, "Colossal.IO.AssetDatabase.dll"), readerParams);

// --- Patch 10: Backtrace.Unity.dll (ReadAllBytes sharing violation) ---
totalPatches += PatchBacktrace(Path.Combine(managedPath, "Backtrace.Unity.dll"), readerParams);

// ============================================================
// SECTION 2: Config Changes
// ============================================================

// --- boot.config: Disable graphics jobs ---
PatchBootConfig(bootConfigPath);

// --- Registry: LongPathsEnabled ---
PatchRegistry(systemRegPath);

// --- Bottle env: UNITY_DISABLE_GRAPHICS_JOBS, WINEDEBUG ---
PatchBottleConf(bottleConfPath);

// --- Crash handler: Rename ---
DisableCrashHandler(crashHandlerPath);

// ============================================================
// Summary
// ============================================================
Console.WriteLine();
Console.WriteLine($"=== Done: {totalPatches} DLL patch(es) applied ===");
Console.WriteLine("Launch CS2 through CrossOver to test.");

// ============================================================
// Patch Implementations
// ============================================================

int PatchColossalIO(string dllPath, ReaderParameters rp)
{
    Console.WriteLine("[Colossal.IO.dll] Patching FindNextFile error handling + LongPath prefix...");
    if (!BackupAndLoad(dllPath, rp, out var asm)) return 0;

    int count = 0;
    string[] typeNames = ["<EnumerateFileSystemIterator>d__15", "<EnumerateFileSystemIteratorRecursive>d__16"];

    foreach (var typeName in typeNames)
    {
        var stateType = asm!.MainModule.Types.SelectMany(t => t.NestedTypes).FirstOrDefault(t => t.Name == typeName);
        var moveNext = stateType?.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext == null) { Console.WriteLine($"  WARN: {typeName} not found"); continue; }

        foreach (var instr in moveNext.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Ldc_I4_S && (sbyte)instr.Operand == 0x12)
            {
                var idx = moveNext.Body.Instructions.IndexOf(instr);
                var next = moveNext.Body.Instructions[idx + 1];
                if (next.OpCode == OpCodes.Beq_S || next.OpCode == OpCodes.Beq)
                {
                    var prev = moveNext.Body.Instructions[idx - 1];
                    prev.OpCode = OpCodes.Nop; prev.Operand = null;
                    instr.OpCode = OpCodes.Nop; instr.Operand = null;
                    next.OpCode = OpCodes.Br_S;
                    Console.WriteLine($"  Patched {typeName}.MoveNext: FindNextFile error → unconditional branch");
                    count++;
                    break;
                }
            }
        }
    }

    // Patch 11: LongPath.AddLongPathPrefix → no-op (return input path unchanged)
    // Wine's CreateFileW and RemoveDirectoryW fail with \\?\ prefix paths.
    // This method prepends \\?\ to every path before Win32 API calls.
    // CS2 paths under Wine never exceed 260 chars, so the prefix is unnecessary.
    // Fixes: .coc settings read errors for all mods, LongFile.OpenRead failures,
    // "IOException: Success" from GetFileHandle with \\?\ prefix.
    //
    // v1.1.1: Clean ILProcessor rebuild — Clear() all three body collections,
    // append `ldarg.0; ret`, set MaxStackSize=1. The v1.1.0 NOP-in-place approach
    // left orphaned ExceptionHandlers, stale MaxStackSize (3), and dangling branch
    // metadata that Mono's IL verifier rejected with
    // `InvalidProgramException: Invalid IL code in System.IO.LongPath:AddLongPathPrefix
    // (string): IL_0017: nop`, which cascaded into GameManager NREs.
    var longPathType = asm!.MainModule.Types.FirstOrDefault(t => t.Name == "LongPath" && t.Namespace == "System.IO");
    var addPrefix = longPathType?.Methods.FirstOrDefault(m => m.Name == "AddLongPathPrefix");
    if (addPrefix != null)
    {
        var body = addPrefix.Body;
        body.Instructions.Clear();
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 1;
        Console.WriteLine("  Patched LongPath.AddLongPathPrefix: no-op (skip \\\\?\\\\ prefix for Wine)");
        count++;
    }
    else
    {
        Console.WriteLine("  WARN: LongPath.AddLongPathPrefix not found");
    }

    if (count > 0) SaveAssembly(asm!, dllPath);
    return count;
}

int PatchPdxSdk(string dllPath, ReaderParameters rp)
{
    Console.WriteLine("[PDX.SDK.dll] Patching long path operations and File.Delete...");
    if (!BackupAndLoad(dllPath, rp, out var asm)) return 0;

    int count = 0;
    var diskIOType = asm!.MainModule.Types.FirstOrDefault(t => t.Name == "DiskIODefaultWindows");
    if (diskIOType == null) { Console.WriteLine("  WARN: DiskIODefaultWindows not found"); return 0; }

    // Patch 2: DeleteLongPathDirectory — NOP the throw on RemoveDirectory failure
    var deleteLongDir = diskIOType.Methods.FirstOrDefault(m => m.Name == "DeleteLongPathDirectory");
    if (deleteLongDir != null)
    {
        foreach (var instr in deleteLongDir.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Throw)
            {
                // Find the newobj IOException before the throw
                var idx = deleteLongDir.Body.Instructions.IndexOf(instr);
                for (int i = idx - 1; i >= 0 && i >= idx - 5; i--)
                {
                    var prev = deleteLongDir.Body.Instructions[i];
                    if (prev.OpCode == OpCodes.Newobj && prev.Operand?.ToString()?.Contains("IOException") == true)
                    {
                        // NOP everything from newobj to just before throw, then change throw to ret
                        for (int j = i; j < idx; j++)
                            deleteLongDir.Body.Instructions[j].OpCode = OpCodes.Nop;
                        instr.OpCode = OpCodes.Ret;
                        Console.WriteLine("  Patched DeleteLongPathDirectory: RemoveDirectory failure → return");
                        count++;
                        break;
                    }
                }
                break;
            }
        }
    }

    // Patch 3: DeleteLongPathFile — NOP the throw on DeleteFileW failure
    var deleteLongFile = diskIOType.Methods.FirstOrDefault(m => m.Name == "DeleteLongPathFile");
    if (deleteLongFile != null)
    {
        foreach (var instr in deleteLongFile.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Throw)
            {
                var idx = deleteLongFile.Body.Instructions.IndexOf(instr);
                for (int i = idx - 1; i >= 0 && i >= idx - 5; i--)
                {
                    var prev = deleteLongFile.Body.Instructions[i];
                    if (prev.OpCode == OpCodes.Newobj && prev.Operand?.ToString()?.Contains("IOException") == true)
                    {
                        for (int j = i; j < idx; j++)
                            deleteLongFile.Body.Instructions[j].OpCode = OpCodes.Nop;
                        instr.OpCode = OpCodes.Ret;
                        Console.WriteLine("  Patched DeleteLongPathFile: DeleteFileW failure → return");
                        count++;
                        break;
                    }
                }
                break;
            }
        }
    }

    // Patch 4: IsLongPath → always return true
    // Forces directory operations (ListDirectories, CreateDirectory, Delete, PathExists, etc.)
    // through Win32 P/Invoke path, bypassing broken System.IO.Directory.* APIs on Wine.
    // File I/O (CreateFileStream) is separately routed back to standard .NET in Patch 8.
    // Forces ALL filesystem operations through Win32 P/Invoke path, bypassing broken System.IO
    var isLongPath = diskIOType.Methods.FirstOrDefault(m => m.Name == "IsLongPath");
    if (isLongPath != null)
    {
        var instrs = isLongPath.Body.Instructions;
        // Replace entire body with: ldc.i4.1; ret
        instrs[0].OpCode = OpCodes.Ldc_I4_1;
        instrs[0].Operand = null;
        instrs[1].OpCode = OpCodes.Ret;
        instrs[1].Operand = null;
        for (int i = 2; i < instrs.Count; i++)
        {
            instrs[i].OpCode = OpCodes.Nop;
            instrs[i].Operand = null;
        }
        Console.WriteLine("  Patched IsLongPath: always returns true (force Win32 API path)");
        count++;
    }

    // Patch 5: CreateLongPathDirectory — NOP throw on kernel32.CreateDirectory failure
    var createLongDir = diskIOType.Methods.FirstOrDefault(m => m.Name == "CreateLongPathDirectory");
    if (createLongDir != null)
    {
        foreach (var instr in createLongDir.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Throw)
            {
                var idx = createLongDir.Body.Instructions.IndexOf(instr);
                for (int i = idx - 1; i >= 0 && i >= idx - 5; i--)
                {
                    var prev = createLongDir.Body.Instructions[i];
                    if (prev.OpCode == OpCodes.Newobj && prev.Operand?.ToString()?.Contains("IOException") == true)
                    {
                        for (int j = i; j < idx; j++)
                            createLongDir.Body.Instructions[j].OpCode = OpCodes.Nop;
                        instr.OpCode = OpCodes.Ret;
                        Console.WriteLine("  Patched CreateLongPathDirectory: CreateDirectory failure → return");
                        count++;
                        break;
                    }
                }
                break;
            }
        }
    }

    // Patch 6: LongPathMove — NOP throw on MoveFileW failure
    var longPathMove = diskIOType.Methods.FirstOrDefault(m => m.Name == "LongPathMove");
    if (longPathMove != null)
    {
        foreach (var instr in longPathMove.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Throw)
            {
                var idx = longPathMove.Body.Instructions.IndexOf(instr);
                for (int i = idx - 1; i >= 0 && i >= idx - 5; i--)
                {
                    var prev = longPathMove.Body.Instructions[i];
                    if (prev.OpCode == OpCodes.Newobj && prev.Operand?.ToString()?.Contains("IOException") == true)
                    {
                        for (int j = i; j < idx; j++)
                            longPathMove.Body.Instructions[j].OpCode = OpCodes.Nop;
                        instr.OpCode = OpCodes.Ret;
                        Console.WriteLine("  Patched LongPathMove: MoveFileW failure → return");
                        count++;
                        break;
                    }
                }
                break;
            }
        }
    }

    // Patch 7: CreateFileStream — force standard .NET FileStream path (not CreateLongPathFileStream)
    // Wine's CreateFileW with \\?\ prefix returns INVALID_HANDLE_VALUE for some file operations
    // (particularly CREATE_ALWAYS for new files). This causes ObjectDisposedException downstream.
    // Standard new FileStream(path, mode, access, share) works fine on Wine.
    // Fix: In CreateFileStream, change brtrue.s (go to long path) to brfalse.s (never go to long path
    // since IsLongPath always returns true). This makes CreateFileStream always use standard .NET
    // FileStream while all other methods (ListDirectories, CreateDirectory, etc.) stay on Win32 APIs.
    // Ref: Wine kernelbase/file.c, Mono issue #12783 (SafeFileHandle + FileStream)
    var createFileStreamMethod = diskIOType.Methods.FirstOrDefault(
        m => m.Name == "CreateFileStream" && m.Parameters.Count == 4);
    if (createFileStreamMethod != null)
    {
        foreach (var instr in createFileStreamMethod.Body.Instructions)
        {
            // Find: brtrue.s that follows IsLongPath call
            if ((instr.OpCode == OpCodes.Brtrue_S || instr.OpCode == OpCodes.Brtrue) &&
                instr.Previous?.Operand?.ToString()?.Contains("IsLongPath") == true)
            {
                instr.OpCode = OpCodes.Brfalse_S;
                // Operand stays same (target = CreateLongPathFileStream path, never taken)
                Console.WriteLine("  Patched CreateFileStream: force standard .NET FileStream (skip Wine CreateFile bug)");
                count++;
                break;
            }
        }
    }

    if (count > 0) SaveAssembly(asm!, dllPath);
    return count;
}

int PatchAssetDatabase(string dllPath, ReaderParameters rp)
{
    Console.WriteLine("[Colossal.IO.AssetDatabase.dll] Patching .priority file handling...");
    if (!BackupAndLoad(dllPath, rp, out var asm)) return 0;

    int count = 0;
    var fsdsType = asm!.MainModule.Types.FirstOrDefault(t => t.Name == "FileSystemDataSource");
    if (fsdsType == null) { Console.WriteLine("  WARN: FileSystemDataSource not found"); return 0; }

    // Fix: Wine's File.Exists returns true for non-existent .priority file.
    // Instead of try-catch (which produces invalid IL due to stack issues with leave),
    // change the File.Exists call to always return false by replacing:
    //   call File.Exists  →  pop (consume path) + ldc.i4.0 (push false)
    // The brfalse after it will then always skip the .priority reading.
    // .priority is just a load-order optimization hint — safe to skip.
    var populateMethod = fsdsType.Methods.FirstOrDefault(m => m.Name == "PopulateFromDirectory");
    if (populateMethod != null)
    {
        var instrs = populateMethod.Body.Instructions;
        for (int i = 0; i < instrs.Count; i++)
        {
            if (instrs[i].OpCode == OpCodes.Call &&
                instrs[i].Operand?.ToString()?.Contains("System.IO.File::Exists") == true)
            {
                // Check if the next instruction after some gap is ReadAllLines (to confirm this is the .priority check)
                // The pattern is: ldstr ".priority" ... call File.Exists ... brfalse ... call ReadAllLines
                bool foundPriority = false;
                for (int j = Math.Max(0, i - 5); j < i; j++)
                {
                    if (instrs[j].OpCode == OpCodes.Ldstr && instrs[j].Operand?.ToString() == ".priority")
                    {
                        foundPriority = true;
                        break;
                    }
                }
                if (!foundPriority) continue;

                // Replace: call File.Exists → pop (consume path string) + ldc.i4.0 (push false)
                instrs[i].OpCode = OpCodes.Pop;
                instrs[i].Operand = null;
                // Insert ldc.i4.0 after pop
                var il = populateMethod.Body.GetILProcessor();
                il.InsertAfter(instrs[i], il.Create(OpCodes.Ldc_I4_0));
                Console.WriteLine("  Patched PopulateFromDirectory: .priority File.Exists → always false");
                count++;
                break;
            }
        }
    }

    if (count > 0) SaveAssembly(asm!, dllPath);
    return count;
}

int PatchBacktrace(string dllPath, ReaderParameters rp)
{
    Console.WriteLine("[Backtrace.Unity.dll] Patching crash reporter attachment handling...");
    if (!BackupAndLoad(dllPath, rp, out var asm)) return 0;

    int count = 0;
    var httpClientType = asm!.MainModule.Types.FirstOrDefault(t => t.Name == "BacktraceHttpClient");
    if (httpClientType == null) { Console.WriteLine("  WARN: BacktraceHttpClient not found"); return 0; }

    // Fix: Wine's file locking causes sharing violations when Backtrace tries to
    // attach log files. Instead of try-catch (which produces invalid IL), make the
    // entire method a no-op by inserting ret at the start. Crash report attachments
    // are telemetry, not gameplay.
    var addAttachMethod = httpClientType.Methods.FirstOrDefault(m => m.Name == "AddAttachmentToFormData");
    if (addAttachMethod != null)
    {
        var il = addAttachMethod.Body.GetILProcessor();
        var firstInstr = addAttachMethod.Body.Instructions[0];
        il.InsertBefore(firstInstr, il.Create(OpCodes.Ret));
        Console.WriteLine("  Patched AddAttachmentToFormData: early return (skip Wine sharing violation)");
        count++;
    }

    if (count > 0) SaveAssembly(asm!, dllPath);
    return count;
}

bool BackupAndLoad(string dllPath, ReaderParameters rp, out AssemblyDefinition? asm)
{
    asm = null;
    if (!File.Exists(dllPath)) { Console.WriteLine($"  SKIP: {Path.GetFileName(dllPath)} not found"); return false; }

    var backupPath = dllPath + ".backup";
    if (!File.Exists(backupPath))
    {
        File.Copy(dllPath, backupPath);
        Console.WriteLine($"  Backup: {Path.GetFileName(backupPath)}");
    }
    else
    {
        // Restore from backup first to ensure we're patching the original
        File.Copy(backupPath, dllPath, overwrite: true);
        Console.WriteLine($"  Restored from backup for clean patching");
    }

    asm = AssemblyDefinition.ReadAssembly(dllPath, rp);
    return true;
}

void SaveAssembly(AssemblyDefinition asm, string dllPath)
{
    asm.Write(dllPath, new WriterParameters());
    asm.Dispose();
}

void PatchBootConfig(string path)
{
    if (!File.Exists(path)) { Console.WriteLine("[boot.config] SKIP: not found"); return; }

    var backupPath = path + ".backup";
    if (!File.Exists(backupPath)) File.Copy(path, backupPath);

    var content = File.ReadAllText(path);
    var patched = content
        .Replace("gfx-enable-gfx-jobs=1", "gfx-enable-gfx-jobs=0")
        .Replace("gfx-enable-native-gfx-jobs=1", "gfx-enable-native-gfx-jobs=0");

    if (patched != content)
    {
        File.WriteAllText(path, patched);
        Console.WriteLine("[boot.config] Disabled graphics jobs");
    }
    else
    {
        Console.WriteLine("[boot.config] Already patched");
    }
}

void PatchRegistry(string path)
{
    if (!File.Exists(path)) { Console.WriteLine("[system.reg] SKIP: not found"); return; }

    var content = File.ReadAllText(path);
    if (content.Contains("LongPathsEnabled"))
    {
        Console.WriteLine("[system.reg] LongPathsEnabled already set");
        return;
    }

    // Primary: insert after NtfsDisableLastAccessUpdate
    var patched = content.Replace(
        "\"NtfsDisableLastAccessUpdate\"=dword:80000002",
        "\"NtfsDisableLastAccessUpdate\"=dword:80000002\n\"LongPathsEnabled\"=dword:00000001"
    );

    // Fallback: insert after the Filesystem section header
    if (patched == content)
    {
        patched = content.Replace(
            "[System\\\\CurrentControlSet\\\\Control\\\\Filesystem]",
            "[System\\\\CurrentControlSet\\\\Control\\\\Filesystem]\n\"LongPathsEnabled\"=dword:00000001"
        );
    }

    if (patched != content)
    {
        File.WriteAllText(path, patched);
        Console.WriteLine("[system.reg] Added LongPathsEnabled=1");
    }
    else
    {
        Console.WriteLine("[system.reg] WARN: Could not find insertion point for LongPathsEnabled");
    }
}

void PatchBottleConf(string path)
{
    if (!File.Exists(path)) { Console.WriteLine("[cxbottle.conf] SKIP: not found"); return; }

    var content = File.ReadAllText(path);
    var original = content;

    // Each env var: check if already present, add if not
    string[][] envVars =
    [
        ["UNITY_DISABLE_GRAPHICS_JOBS", "1"],
        ["WINEDEBUG", "-all"],
    ];

    foreach (var ev in envVars)
    {
        if (content.Contains(ev[0]))
        {
            Console.WriteLine($"[cxbottle.conf] {ev[0]} already set");
            continue;
        }

        var line = $"\"{ev[0]}\" = \"{ev[1]}\"";

        // Primary: insert after CX_GRAPHICS_BACKEND line
        var patched = content.Replace(
            "\"CX_GRAPHICS_BACKEND\" = \"d3dmetal\"",
            $"\"CX_GRAPHICS_BACKEND\" = \"d3dmetal\"\n{line}"
        );

        // Fallback: insert after [EnvironmentVariables] section header
        if (patched == content)
        {
            patched = content.Replace(
                "[EnvironmentVariables]",
                $"[EnvironmentVariables]\n{line}"
            );
        }

        if (patched != content)
        {
            content = patched;
            Console.WriteLine($"[cxbottle.conf] Added {ev[0]}={ev[1]}");
        }
        else
        {
            Console.WriteLine($"[cxbottle.conf] WARN: Could not find insertion point for {ev[0]}");
        }
    }

    if (content != original)
        File.WriteAllText(path, content);
}

void DisableCrashHandler(string path)
{
    if (!File.Exists(path)) { Console.WriteLine("[UnityCrashHandler64] Already disabled or not found"); return; }

    File.Move(path, path + ".disabled", overwrite: true);
    Console.WriteLine("[UnityCrashHandler64] Renamed to .disabled");
}

// ============================================================
// Rollback Mode
// ============================================================

void RollbackAll()
{
    Console.WriteLine("=== CS2 CrossOver macOS Patcher — ROLLBACK MODE ===");
    Console.WriteLine($"Bottle: {bottlePath}");
    Console.WriteLine($"Game:   {gamePath}");
    Console.WriteLine();

    int restored = 0;

    // Restore DLL backups
    string[] dlls = ["Colossal.IO.dll", "PDX.SDK.dll", "Colossal.IO.AssetDatabase.dll", "Backtrace.Unity.dll"];
    foreach (var dll in dlls)
    {
        if (RestoreFromBackup(Path.Combine(managedPath, dll))) restored++;
    }

    // Restore boot.config
    if (RestoreFromBackup(bootConfigPath)) restored++;

    // Revert cxbottle.conf: strip WINEDEBUG and MONO_GC_PARAMS lines
    if (RollbackBottleConf(bottleConfPath)) restored++;

    // Restore UnityCrashHandler64.exe
    var disabledCrashHandler = crashHandlerPath + ".disabled";
    if (File.Exists(disabledCrashHandler))
    {
        File.Move(disabledCrashHandler, crashHandlerPath, overwrite: true);
        Console.WriteLine("[UnityCrashHandler64.exe] Restored from .disabled");
        restored++;
    }
    else
    {
        Console.WriteLine("[UnityCrashHandler64.exe] Already in place (or never disabled)");
    }

    Console.WriteLine();
    Console.WriteLine($"=== Rollback complete: {restored} item(s) restored ===");
    Console.WriteLine("Backup files preserved so a subsequent normal run can re-patch cleanly.");
}

bool RestoreFromBackup(string path)
{
    var backup = path + ".backup";
    if (!File.Exists(backup))
    {
        Console.WriteLine($"[{Path.GetFileName(path)}] No backup found, skipping");
        return false;
    }
    File.Copy(backup, path, overwrite: true);
    Console.WriteLine($"[{Path.GetFileName(path)}] Restored from backup");
    return true;
}

bool RollbackBottleConf(string path)
{
    if (!File.Exists(path))
    {
        Console.WriteLine("[cxbottle.conf] SKIP: not found");
        return false;
    }
    var content = File.ReadAllText(path);
    var lines = content.Split('\n');
    var filtered = lines
        .Where(l => !l.Contains("\"WINEDEBUG\"") && !l.Contains("\"MONO_GC_PARAMS\""))
        .ToArray();
    var patched = string.Join("\n", filtered);

    if (patched != content)
    {
        File.WriteAllText(path, patched);
        Console.WriteLine("[cxbottle.conf] Removed WINEDEBUG and MONO_GC_PARAMS lines");
        return true;
    }
    Console.WriteLine("[cxbottle.conf] No WINEDEBUG/MONO_GC_PARAMS lines to remove");
    return false;
}
