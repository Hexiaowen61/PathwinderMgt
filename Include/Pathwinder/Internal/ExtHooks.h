/***************************************************************************************************
 * Pathwinder
 *   Path redirection for files, directories, and registry entries.
 ***************************************************************************************************
 * @file ExtHooks.h
 *   Declarations for the Windows API hooks added by this fork. They live here rather than in
 *   Hooks.h so that the upstream header stays untouched and upstream changes merge cleanly.
 **************************************************************************************************/

#pragma once

#include "Hooks.h"

namespace Pathwinder
{
  namespace Hooks
  {
    // https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw
    // Executables located in redirected directories cannot be launched without this hook because
    // the kernel resolves the image path itself, without involving any of the file hooks above.
    // The hook is placed here rather than at the underlying `NtCreateUserProcess` because the
    // caller holds locks during that system call that make even routine work (rule lookup, file
    // queries, logging) deadlock.
    PROTECTED_HOOKSHOT_DYNAMIC_HOOK_FROM_TYPESPEC(
        CreateProcessW,
        BOOL(__stdcall)(
            LPCWSTR,
            LPWSTR,
            LPSECURITY_ATTRIBUTES,
            LPSECURITY_ATTRIBUTES,
            BOOL,
            DWORD,
            LPVOID,
            LPCWSTR,
            LPSTARTUPINFOW,
            LPPROCESS_INFORMATION));

    // Shell identifiers are parsed from a path before the shell is asked to act on them, and the
    // resulting identifier is interpreted by Explorer in its own process. Creating it from the
    // redirected path keeps the two processes looking at the same files.
    HOOKSHOT_DYNAMIC_HOOK_FROM_FUNCTION(SHCreateItemFromParsingName);
    HOOKSHOT_DYNAMIC_HOOK_FROM_FUNCTION(SHOpenFolderAndSelectItems);

    // An application that hands a path to the shell through the shell namespace rather than through
    // ShellExecute parses it into an identifier first. That parse is the last point inside this
    // process at which the path is still a plain string, so it is the only place where an origin-
    // drive path can be replaced with its redirected target before it leaves the process.
    HOOKSHOT_DYNAMIC_HOOK_FROM_FUNCTION(SHParseDisplayName);
    HOOKSHOT_DYNAMIC_HOOK_FROM_FUNCTION(ILCreateFromPathW);

    // A redirected file is actually stored on a different volume, and the system reports that
    // volume to anything that asks. An application that canonicalizes a path before acting on it
    // therefore sees a location that does not match the path it recorded, and stops before
    // reaching the shell, which is why opening a redirected file silently does nothing. Reporting
    // the origin path and volume keeps the application's view of the filesystem consistent.
    HOOKSHOT_DYNAMIC_HOOK_FROM_FUNCTION(GetFinalPathNameByHandleW);
    HOOKSHOT_DYNAMIC_HOOK_FROM_FUNCTION(GetVolumePathNameW);

  } // namespace Hooks
} // namespace Pathwinder
