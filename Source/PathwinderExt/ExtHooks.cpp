/***************************************************************************************************
 * Pathwinder
 *   Path redirection for files, directories, and registry entries.
 ***************************************************************************************************
 * @file ExtHooks.cpp
 *   Implementations of the Windows API hooks added by this fork. The code lives here rather than
 *   in the upstream files so that upstream changes merge with minimal conflicts.
 ***********************************************************************************************/

#include <atomic>
#include <cwchar>
#include <functional>
#include <new>
#include <optional>
#include <string>

#include <Hookshot/DynamicHook.h>
#include <Infra/Core/DebugAssert.h>
#include <Infra/Core/Message.h>
#include <Infra/Core/Strings.h>
#include <Infra/Core/TemporaryBuffer.h>

#include "ApiWindows.h"
#include "ExtInternal.h"
#include "FileInformationStruct.h"
#include "FilesystemDirector.h"
#include "FilesystemExecutor.h"
#include "FilesystemInstruction.h"
#include "FilesystemOperations.h"
#include "ExtHooks.h"
#include "OpenHandleStore.h"
#include "Strings.h"

using Pathwinder::Hooks::Ext::FilesystemDirectorInstance;
using Pathwinder::Hooks::Ext::GetRequestIdentifier;
using Pathwinder::Hooks::Ext::OpenHandleStoreInstance;

/// Determines if the specified file exists and is not a directory.
/// Used to decide whether an overlay-mode image path redirection is safe to apply. The Win32 call
/// updates the calling thread's last error value, which belongs to the caller, so it is preserved
/// across this function in order to leave no trace on the invoking application.
/// @param [in] path Absolute path to check.
/// @return `true` if a file exists at the specified path, `false` otherwise.
[[maybe_unused]] static bool FileExists(std::wstring_view path)
{
  const DWORD lastError = GetLastError();

  const std::wstring nullTerminatedPath(path.begin(), path.end());
  const DWORD attributes = GetFileAttributesW(nullTerminatedPath.c_str());

  SetLastError(lastError);

  return (INVALID_FILE_ATTRIBUTES != attributes) &&
      (0 == (attributes & FILE_ATTRIBUTE_DIRECTORY));
}

/// Determines the path that should be used when a path is about to leave this process, applying a
/// redirection rule when one matches. Everything that receives a path this way (a newly created
/// process, Explorer, an associated application, or a shell identifier) runs outside of this
/// process and therefore sees the real filesystem rather than the illusion that Pathwinder presents
/// here. An origin-drive path handed over unchanged points at a location where nothing exists,
/// which is why opening a redirected file or its containing folder silently does nothing.
/// @param [in] path Absolute path as requested by the caller.
/// @return Redirected path without a Windows namespace prefix, or no value if the path is not
/// an absolute drive-letter path, does not match any rule, or is not a file system path at all.
static std::optional<std::wstring> LookupRedirectedTargetPath(std::wstring_view path)
{
  // Only drive-letter paths take part in redirection. This also filters out URLs, bare file names,
  // and shell namespace identifiers such as "shell:AppsFolder".
  if ((path.size() < 3) || (0 == iswalpha(path[0])) || (L':' != path[1]) ||
      ((L'\\' != path[2]) && (L'/' != path[2])))
  {
    return std::nullopt;
  }

  const Pathwinder::FileOperationInstruction instruction =
      FilesystemDirectorInstance().GetInstructionForFileOperation(
          path,
          Pathwinder::FileAccessMode::ReadOnly(),
          Pathwinder::CreateDisposition::OpenExistingFile());

  if (false == instruction.HasRedirectedFilename()) return std::nullopt;

  // The redirected filename carries a Windows namespace prefix, but both process creation and
  // shell APIs expect a plain absolute path, so the prefix is removed.
  std::wstring_view redirectedPath = instruction.GetRedirectedFilename();
  redirectedPath.remove_prefix(
      Pathwinder::Strings::PathGetWindowsNamespacePrefix(redirectedPath).length());

  return std::wstring(redirectedPath);
}

/// Rewrites every absolute path that appears as a standalone token inside an argument string.
/// Argument strings are received verbatim, either by a newly created process or by the shell, and
/// neither is redirected, so every path that would otherwise point at an origin directory has to
/// be replaced with its redirected target.
/// @param [in] original Argument string as supplied by the caller.
/// @param [in] functionName Name of the hook function, used for logging.
/// @param [in] requestIdentifier Identifier for this hook invocation, used for logging.
/// @param [out] firstTokenRedirected Set to `true` if a path at the first token was redirected.
/// @return Rebuilt argument string if any path was redirected, otherwise no value.
static std::optional<std::wstring> RewriteEmbeddedPaths(
    std::wstring_view original,
    const wchar_t* functionName,
    unsigned int requestIdentifier,
    bool& firstTokenRedirected)
{
  // The first token holds the executable image or the shell target. It may be preceded by
  // whitespace and wrapped in quotes, so its index is not necessarily zero.
  size_t firstTokenIndex = 0;

  while ((firstTokenIndex < original.size()) && (L' ' == original[firstTokenIndex]))
  {
    ++firstTokenIndex;
  }

  if ((firstTokenIndex < original.size()) && (L'"' == original[firstTokenIndex]))
  {
    ++firstTokenIndex;
  }

  std::wstring rebuilt;
  rebuilt.reserve(original.size() + 256);

  size_t copiedUpTo = 0;
  size_t index = 0;

  while ((index + 2) < original.size())
  {
    const bool startsArgument =
        (0 == index) || (L'"' == original[index - 1]) || (L'=' == original[index - 1]) ||
        (L',' == original[index - 1]) || (L' ' == original[index - 1]);

    if ((true == startsArgument) && (0 != iswalpha(original[index])) &&
        (L':' == original[index + 1]) && (L'\\' == original[index + 2]))
    {
      size_t pathEnd = index;

      while ((pathEnd < original.size()) && (L'"' != original[pathEnd]) &&
             (L' ' != original[pathEnd]))
      {
        ++pathEnd;
      }

      const std::wstring_view path(original.data() + index, pathEnd - index);
      const std::optional<std::wstring> redirectedPath = LookupRedirectedTargetPath(path);

      if (true == redirectedPath.has_value())
      {
        rebuilt.append(original.substr(copiedUpTo, index - copiedUpTo));
        rebuilt.append(*redirectedPath);
        copiedUpTo = pathEnd;

        if (firstTokenIndex == index) firstTokenRedirected = true;

        Infra::Message::OutputFormatted(
            Infra::Message::ESeverity::Info,
            L"%s(%u): Redirecting embedded path from \"%.*s\" to \"%s\".",
            functionName,
            requestIdentifier,
            static_cast<int>(path.length()),
            path.data(),
            redirectedPath->c_str());
      }

      index = pathEnd;
    }
    else
    {
      ++index;
    }
  }

  if (copiedUpTo > 0)
  {
    rebuilt.append(original.substr(copiedUpTo));
    return rebuilt;
  }

  return std::nullopt;
}

/// Determines the path that an executable should actually be launched from, applying a redirection
/// rule when one matches and the redirected file exists.
/// @param [in] imagePath Absolute path of the executable as requested by the caller.
/// @return Redirected path if one should be used, otherwise no value.
static std::optional<std::wstring> LookupRedirectedImagePath(std::wstring_view imagePath)
{
  std::optional<std::wstring> redirectedPath = LookupRedirectedTargetPath(imagePath);

  if (false == redirectedPath.has_value()) return std::nullopt;

  // Process creation cannot fall back to another path when the redirected path is unusable: the
  // kernel resolves the image file itself and fails the entire call, and the caller does not retry
  // with the original path. Rewriting the image path to a file that does not exist would therefore
  // turn a launch that works today into a failure, so the redirected file is always checked for
  // existence first and the original path is retained when it is absent.
  if (false == FileExists(*redirectedPath)) return std::nullopt;

  return redirectedPath;
}

/// Converts a narrow character string supplied to an ANSI Windows API function into wide
/// characters. ANSI entry points are legacy but still common, and the redirection rules are always
/// expressed in wide characters.
/// @param [in] str Null-terminated narrow character string to convert.
/// @return Converted string, or an empty string on failure.
static std::wstring ConvertAnsiStringToWide(const char* str)
{
  const int length = MultiByteToWideChar(CP_ACP, 0, str, -1, nullptr, 0);

  if (length <= 1) return std::wstring();

  std::wstring converted(static_cast<size_t>(length - 1), L'\0');

  if (0 == MultiByteToWideChar(CP_ACP, 0, str, -1, converted.data(), length))
  {
    return std::wstring();
  }

  return converted;
}

/// Converts a wide character string into the ANSI code page used by an ANSI Windows API function.
/// @param [in] str String to convert.
/// @return Converted string, or no value on failure.
static std::optional<std::string> ConvertWideStringToAnsi(std::wstring_view str)
{
  const int length = WideCharToMultiByte(
      CP_ACP,
      0,
      str.data(),
      static_cast<int>(str.size()),
      nullptr,
      0,
      nullptr,
      nullptr);

  if (length <= 0) return std::nullopt;

  std::string converted(static_cast<size_t>(length), '\0');

  if (0 == WideCharToMultiByte(
               CP_ACP,
               0,
               str.data(),
               static_cast<int>(str.size()),
               converted.data(),
               length,
               nullptr,
               nullptr))
  {
    return std::nullopt;
  }

  return converted;
}

/// ANSI version of LookupRedirectedTargetPath.
/// @param [in] path Null-terminated narrow character path as requested by the caller.
/// @return Redirected path in narrow characters, or no value if no redirection applies.
static std::optional<std::string> LookupRedirectedAnsiPath(const char* path)
{
  const std::wstring widePath = ConvertAnsiStringToWide(path);

  if (true == widePath.empty()) return std::nullopt;

  const std::optional<std::wstring> redirectedPath = LookupRedirectedTargetPath(widePath);

  if (false == redirectedPath.has_value()) return std::nullopt;

  return ConvertWideStringToAnsi(*redirectedPath);
}

/// ANSI version of RewriteEmbeddedPaths.
/// @param [in] original Null-terminated narrow character argument string.
/// @param [in] functionName Name of the hook function, used for logging.
/// @param [in] requestIdentifier Identifier for this hook invocation, used for logging.
/// @return Rebuilt narrow character argument string, or no value if no path was redirected.
static std::optional<std::string> RewriteEmbeddedAnsiPaths(
    const char* original, const wchar_t* functionName, unsigned int requestIdentifier)
{
  const std::wstring wideOriginal = ConvertAnsiStringToWide(original);

  if (true == wideOriginal.empty()) return std::nullopt;

  bool unusedFirstTokenRedirected = false;

  const std::optional<std::wstring> rebuilt = RewriteEmbeddedPaths(
      wideOriginal, functionName, requestIdentifier, unusedFirstTokenRedirected);

  if (false == rebuilt.has_value()) return std::nullopt;

  return ConvertWideStringToAnsi(*rebuilt);
}

BOOL Pathwinder::Hooks::DynamicHook_CreateProcessW::Hook(
    LPCWSTR lpApplicationName,
    LPWSTR lpCommandLine,
    LPSECURITY_ATTRIBUTES lpProcessAttributes,
    LPSECURITY_ATTRIBUTES lpThreadAttributes,
    BOOL bInheritHandles,
    DWORD dwCreationFlags,
    LPVOID lpEnvironment,
    LPCWSTR lpCurrentDirectory,
    LPSTARTUPINFOW lpStartupInfo,
    LPPROCESS_INFORMATION lpProcessInformation)
{
  // The executable path is supplied either directly as `lpApplicationName`, or as the first token
  // of `lpCommandLine` when the former is null. Both forms are redirected so that the two remain
  // consistent. The caller's buffers are never modified: replacement strings are owned by this
  // function and merely passed to the original function.
  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s(%u): lpApplicationName=\"%s\", lpCommandLine=\"%s\", lpCurrentDirectory=\"%s\".",
      GetFunctionName(),
      GetRequestIdentifier(),
      (nullptr != lpApplicationName) ? lpApplicationName : L"<null>",
      (nullptr != lpCommandLine) ? lpCommandLine : L"<null>",
      (nullptr != lpCurrentDirectory) ? lpCurrentDirectory : L"<null>");

  std::optional<std::wstring> applicationNameReplacement;
  std::optional<std::wstring> commandLineReplacement;
  bool imageRedirected = false;

  if (nullptr != lpApplicationName)
  {
    applicationNameReplacement = LookupRedirectedImagePath(lpApplicationName);

    if (true == applicationNameReplacement.has_value())
    {
      imageRedirected = true;

      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s(%u): Redirecting process image path from \"%s\" to \"%s\".",
          GetFunctionName(),
          GetRequestIdentifier(),
          lpApplicationName,
          applicationNameReplacement->c_str());

      lpApplicationName = applicationNameReplacement->c_str();
    }
  }

  // Every path that appears on the command line has to be redirected, not just the executable.
  // Programs such as the WeChat plugin host receive directories through arguments like
  // "--flue-runtime-dir=<path>" and open them directly, without going through any hook, so leaving
  // them pointing at an origin directory that does not exist on this machine makes the child
  // process abort right after it starts. This must run regardless of whether `lpApplicationName`
  // was supplied, because callers that pass an explicit application name may still embed
  // origin-drive paths (e.g. crashpad's "--database=<path>") in the command line.
  if (nullptr != lpCommandLine)
  {
    // A path at the first token is the executable image (when lpApplicationName is null), and it
    // is what identifies this process image as redirected.
    commandLineReplacement = RewriteEmbeddedPaths(
        lpCommandLine, GetFunctionName(), GetRequestIdentifier(), imageRedirected);

    if (true == commandLineReplacement.has_value())
    {
      lpCommandLine = const_cast<LPWSTR>(commandLineReplacement->c_str());
    }
  }

  // The current directory is redirected as well. The kernel applies it while creating the process,
  // before any of this process' hooks can intercept the lookup, so an origin directory that is
  // absent on this machine makes the whole call fail even when the executable was redirected
  // successfully. Unlike an image path, a directory is a valid target here, so its existence check
  // does not exclude directories.
  std::optional<std::wstring> currentDirectoryReplacement;

  if (nullptr != lpCurrentDirectory)
  {
    const Pathwinder::FileOperationInstruction instruction =
        FilesystemDirectorInstance().GetInstructionForFileOperation(
            lpCurrentDirectory,
            Pathwinder::FileAccessMode::ReadOnly(),
            Pathwinder::CreateDisposition::OpenExistingFile());

    if (true == instruction.HasRedirectedFilename())
    {
      std::wstring_view redirectedPath = instruction.GetRedirectedFilename();
      redirectedPath.remove_prefix(
          Pathwinder::Strings::PathGetWindowsNamespacePrefix(redirectedPath).length());

      std::wstring redirectedDirectory(redirectedPath);

      const DWORD lastError = GetLastError();
      const DWORD attributes = GetFileAttributesW(redirectedDirectory.c_str());
      SetLastError(lastError);

      if (INVALID_FILE_ATTRIBUTES != attributes)
      {
        Infra::Message::OutputFormatted(
            Infra::Message::ESeverity::Info,
            L"%s(%u): Redirecting current directory from \"%s\" to \"%s\".",
            GetFunctionName(),
            GetRequestIdentifier(),
            lpCurrentDirectory,
            redirectedDirectory.c_str());

        currentDirectoryReplacement = std::move(redirectedDirectory);
        lpCurrentDirectory = currentDirectoryReplacement->c_str();
      }
    }
  }

  // Rewrite the environment block so that child processes which derive data paths from
  // environment variables (e.g. %APPDATA%, %LOCALAPPDATA%, %USERPROFILE%, %TEMP%) also see
  // the redirected locations. Newer WeChatAppEx builds no longer receive every data directory
  // through the command line; they derive some from environment variables and open them directly
  // without going through any hook. Leaving those pointing at the origin drive (where the data
  // does not exist) makes the child process abort right after start.
  //
  // This is only done when the child process image itself was redirected. Otherwise unrelated
  // applications (e.g. browsers launched from their own install directory) would inherit
  // APPDATA/LOCALAPPDATA pointing at another application's redirected data store and break.
  std::wstring rebuiltEnvironmentBlock;
  LPVOID environmentToUse = lpEnvironment;

  if (imageRedirected)
  {
    LPWCH envBlock = nullptr;
    bool mustFreeEnvBlock = false;

    if (nullptr == lpEnvironment)
    {
      envBlock = GetEnvironmentStringsW();
      mustFreeEnvBlock = true;
    }
    else
    {
      envBlock = reinterpret_cast<LPWCH>(lpEnvironment);
    }

    if (nullptr != envBlock)
    {
      rebuiltEnvironmentBlock.reserve(32768);
      bool rewroteAny = false;

      for (LPWSTR entry = envBlock; L'\0' != *entry;)
      {
        const std::wstring_view entryView(entry);
        const auto eqPos = entryView.find(L'=');

        if ((std::wstring_view::npos != eqPos) && (eqPos > 0))
        {
          const std::wstring_view value = entryView.substr(eqPos + 1);

          // Only rewrite values that look like absolute drive-letter paths.
          if (value.size() >= 3 && 0 != iswalpha(value[0]) && L':' == value[1] &&
              (L'\\' == value[2] || L'/' == value[2]))
          {
            const Pathwinder::FileOperationInstruction instruction =
                FilesystemDirectorInstance().GetInstructionForFileOperation(
                    value,
                    Pathwinder::FileAccessMode::ReadOnly(),
                    Pathwinder::CreateDisposition::OpenExistingFile());

            if (true == instruction.HasRedirectedFilename())
            {
              std::wstring_view redirectedPath = instruction.GetRedirectedFilename();
              redirectedPath.remove_prefix(
                  Pathwinder::Strings::PathGetWindowsNamespacePrefix(redirectedPath).length());

              rebuiltEnvironmentBlock.append(entryView.substr(0, eqPos + 1));
              rebuiltEnvironmentBlock.append(redirectedPath);
              rebuiltEnvironmentBlock.push_back(L'\0');
              rewroteAny = true;

              Infra::Message::OutputFormatted(
                  Infra::Message::ESeverity::Info,
                  L"%s(%u): Redirecting environment variable \"%.*s\" from \"%.*s\" to \"%s\".",
                  GetFunctionName(),
                  GetRequestIdentifier(),
                  static_cast<int>(eqPos),
                  entryView.data(),
                  static_cast<int>(value.length()),
                  value.data(),
                  redirectedPath.data());

              entry += entryView.size() + 1;
              continue;
            }
          }
        }

        rebuiltEnvironmentBlock.append(entryView);
        rebuiltEnvironmentBlock.push_back(L'\0');
        entry += entryView.size() + 1;
      }

      rebuiltEnvironmentBlock.push_back(L'\0');

      if (mustFreeEnvBlock)
      {
        FreeEnvironmentStringsW(envBlock);
      }

      if (rewroteAny)
      {
        environmentToUse = rebuiltEnvironmentBlock.data();
      }
    }
  }

  const BOOL callSucceeded = Original(
      lpApplicationName,
      lpCommandLine,
      lpProcessAttributes,
      lpThreadAttributes,
      bInheritHandles,
      dwCreationFlags,
      environmentToUse,
      lpCurrentDirectory,
      lpStartupInfo,
      lpProcessInformation);

  const DWORD lastError = GetLastError();

  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s(%u): CreateProcessW returned %s (lastError=%lu, processId=%lu).",
      GetFunctionName(),
      GetRequestIdentifier(),
      (TRUE == callSucceeded) ? L"TRUE" : L"FALSE",
      lastError,
      ((TRUE == callSucceeded) && (nullptr != lpProcessInformation))
          ? lpProcessInformation->dwProcessId
          : 0UL);

  return callSucceeded;
}
HINSTANCE Pathwinder::Hooks::DynamicHook_ShellExecuteA::Hook(
    HWND hwnd,
    LPCSTR lpOperation,
    LPCSTR lpFile,
    LPCSTR lpParameters,
    LPCSTR lpDirectory,
    INT nShowCmd)
{
  std::optional<std::string> fileReplacement;
  std::optional<std::string> parametersReplacement;
  std::optional<std::string> directoryReplacement;

  if (nullptr != lpFile)
  {
    fileReplacement = LookupRedirectedAnsiPath(lpFile);

    if (true == fileReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell target from \"%s\" to \"%s\".",
          GetFunctionName(),
          lpFile,
          fileReplacement->c_str());

      lpFile = fileReplacement->c_str();
    }
  }

  if (nullptr != lpParameters)
  {
    parametersReplacement =
        RewriteEmbeddedAnsiPaths(lpParameters, GetFunctionName(), GetRequestIdentifier());

    if (true == parametersReplacement.has_value())
    {
      lpParameters = parametersReplacement->c_str();
    }
  }

  if (nullptr != lpDirectory)
  {
    directoryReplacement = LookupRedirectedAnsiPath(lpDirectory);

    if (true == directoryReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell directory from \"%s\" to \"%s\".",
          GetFunctionName(),
          lpDirectory,
          directoryReplacement->c_str());

      lpDirectory = directoryReplacement->c_str();
    }
  }

  return Original(hwnd, lpOperation, lpFile, lpParameters, lpDirectory, nShowCmd);
}

HINSTANCE Pathwinder::Hooks::DynamicHook_ShellExecuteW::Hook(
    HWND hwnd,
    LPCWSTR lpOperation,
    LPCWSTR lpFile,
    LPCWSTR lpParameters,
    LPCWSTR lpDirectory,
    INT nShowCmd)
{
  std::optional<std::wstring> fileReplacement;
  std::optional<std::wstring> parametersReplacement;
  std::optional<std::wstring> directoryReplacement;

  // Diagnostic: record every shell handoff attempt so the exact arguments can be inspected.
  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s: Entry with file \"%s\", parameters \"%s\", directory \"%s\".",
      GetFunctionName(),
      (nullptr != lpFile) ? lpFile : L"<null>",
      (nullptr != lpParameters) ? lpParameters : L"<null>",
      (nullptr != lpDirectory) ? lpDirectory : L"<null>");

  if (nullptr != lpFile)
  {
    fileReplacement = LookupRedirectedTargetPath(lpFile);

    if (true == fileReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell target from \"%s\" to \"%s\".",
          GetFunctionName(),
          lpFile,
          fileReplacement->c_str());

      lpFile = fileReplacement->c_str();
    }
  }

  if (nullptr != lpParameters)
  {
    bool unusedFirstTokenRedirected = false;

    parametersReplacement = RewriteEmbeddedPaths(
        lpParameters, GetFunctionName(), GetRequestIdentifier(), unusedFirstTokenRedirected);

    if (true == parametersReplacement.has_value())
    {
      lpParameters = parametersReplacement->c_str();
    }
  }

  if (nullptr != lpDirectory)
  {
    directoryReplacement = LookupRedirectedTargetPath(lpDirectory);

    if (true == directoryReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell directory from \"%s\" to \"%s\".",
          GetFunctionName(),
          lpDirectory,
          directoryReplacement->c_str());

      lpDirectory = directoryReplacement->c_str();
    }
  }

  return Original(hwnd, lpOperation, lpFile, lpParameters, lpDirectory, nShowCmd);
}

BOOL Pathwinder::Hooks::DynamicHook_ShellExecuteExA::Hook(SHELLEXECUTEINFOA* pExecInfo)
{
  if (nullptr == pExecInfo) return Original(pExecInfo);

  // The shell consumes the arguments during the call, so the caller's own strings are swapped
  // temporarily and restored afterwards. The redirection therefore stays internal and the caller
  // keeps seeing the origin-drive path it supplied.
  const LPCSTR originalFile = pExecInfo->lpFile;
  const LPCSTR originalParameters = pExecInfo->lpParameters;
  const LPCSTR originalDirectory = pExecInfo->lpDirectory;

  std::optional<std::string> fileReplacement;
  std::optional<std::string> parametersReplacement;
  std::optional<std::string> directoryReplacement;

  if (nullptr != pExecInfo->lpFile)
  {
    fileReplacement = LookupRedirectedAnsiPath(pExecInfo->lpFile);

    if (true == fileReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell target from \"%s\" to \"%s\".",
          GetFunctionName(),
          pExecInfo->lpFile,
          fileReplacement->c_str());

      pExecInfo->lpFile = fileReplacement->c_str();
    }
  }

  if (nullptr != pExecInfo->lpParameters)
  {
    parametersReplacement = RewriteEmbeddedAnsiPaths(
        pExecInfo->lpParameters, GetFunctionName(), GetRequestIdentifier());

    if (true == parametersReplacement.has_value())
    {
      pExecInfo->lpParameters = parametersReplacement->c_str();
    }
  }

  if (nullptr != pExecInfo->lpDirectory)
  {
    directoryReplacement = LookupRedirectedAnsiPath(pExecInfo->lpDirectory);

    if (true == directoryReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell directory from \"%s\" to \"%s\".",
          GetFunctionName(),
          pExecInfo->lpDirectory,
          directoryReplacement->c_str());

      pExecInfo->lpDirectory = directoryReplacement->c_str();
    }
  }

  const BOOL callSucceeded = Original(pExecInfo);

  pExecInfo->lpFile = originalFile;
  pExecInfo->lpParameters = originalParameters;
  pExecInfo->lpDirectory = originalDirectory;

  return callSucceeded;
}

BOOL Pathwinder::Hooks::DynamicHook_ShellExecuteExW::Hook(SHELLEXECUTEINFOW* pExecInfo)
{
  if (nullptr == pExecInfo) return Original(pExecInfo);

  // The shell consumes the arguments during the call, so the caller's own strings are swapped
  // temporarily and restored afterwards. The redirection therefore stays internal and the caller
  // keeps seeing the origin-drive path it supplied.
  const LPCWSTR originalFile = pExecInfo->lpFile;
  const LPCWSTR originalParameters = pExecInfo->lpParameters;
  const LPCWSTR originalDirectory = pExecInfo->lpDirectory;

  // Diagnostic: record every shell handoff attempt so the exact arguments can be inspected.
  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s: Entry with mask=0x%08X, file \"%s\", parameters \"%s\", directory \"%s\", idList=%s.",
      GetFunctionName(),
      static_cast<unsigned int>(pExecInfo->fMask),
      (nullptr != originalFile) ? originalFile : L"<null>",
      (nullptr != originalParameters) ? originalParameters : L"<null>",
      (nullptr != originalDirectory) ? originalDirectory : L"<null>",
      (nullptr != pExecInfo->lpIDList) ? L"<present>" : L"<null>");

  std::optional<std::wstring> fileReplacement;
  std::optional<std::wstring> parametersReplacement;
  std::optional<std::wstring> directoryReplacement;

  if (nullptr != pExecInfo->lpFile)
  {
    fileReplacement = LookupRedirectedTargetPath(pExecInfo->lpFile);

    if (true == fileReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell target from \"%s\" to \"%s\".",
          GetFunctionName(),
          pExecInfo->lpFile,
          fileReplacement->c_str());

      pExecInfo->lpFile = fileReplacement->c_str();
    }
  }

  if (nullptr != pExecInfo->lpParameters)
  {
    bool unusedFirstTokenRedirected = false;

    parametersReplacement = RewriteEmbeddedPaths(
        pExecInfo->lpParameters,
        GetFunctionName(),
        GetRequestIdentifier(),
        unusedFirstTokenRedirected);

    if (true == parametersReplacement.has_value())
    {
      pExecInfo->lpParameters = parametersReplacement->c_str();
    }
  }

  if (nullptr != pExecInfo->lpDirectory)
  {
    directoryReplacement = LookupRedirectedTargetPath(pExecInfo->lpDirectory);

    if (true == directoryReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell directory from \"%s\" to \"%s\".",
          GetFunctionName(),
          pExecInfo->lpDirectory,
          directoryReplacement->c_str());

      pExecInfo->lpDirectory = directoryReplacement->c_str();
    }
  }

  const BOOL callSucceeded = Original(pExecInfo);

  pExecInfo->lpFile = originalFile;
  pExecInfo->lpParameters = originalParameters;
  pExecInfo->lpDirectory = originalDirectory;

  return callSucceeded;
}

HRESULT Pathwinder::Hooks::DynamicHook_SHCreateItemFromParsingName::Hook(
    PCWSTR pszPath, IBindCtx* pbc, REFIID riid, void** ppv)
{
  // Diagnostic: record every shell item creation so the exact path can be inspected.
  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s: Entry with path \"%s\".",
      GetFunctionName(),
      (nullptr != pszPath) ? pszPath : L"<null>");

  std::optional<std::wstring> pathReplacement;

  if (nullptr != pszPath)
  {
    // The resulting shell item is used outside of this process, so it has to be created from the
    // redirected path; an item built from an origin-drive path points at a location that does not
    // exist for Explorer and for any application the shell starts.
    pathReplacement = LookupRedirectedTargetPath(pszPath);

    if (true == pathReplacement.has_value())
    {
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s: Redirecting shell item path from \"%s\" to \"%s\".",
          GetFunctionName(),
          pszPath,
          pathReplacement->c_str());

      pszPath = pathReplacement->c_str();
    }
  }

  return Original(pszPath, pbc, riid, ppv);
}

HRESULT Pathwinder::Hooks::DynamicHook_SHOpenFolderAndSelectItems::Hook(
    PCIDLIST_ABSOLUTE pidlFolder, UINT cidl, PCUITEMID_CHILD_ARRAY apidl, DWORD dwFlags)
{
  std::optional<std::wstring> folderReplacement;
  std::wstring originalFolderPath;

  // Diagnostic: record every shell reveal attempt so the exact arguments can be inspected.
  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s: Entry with folder=%s, cidl=%u, flags=0x%08X.",
      GetFunctionName(),
      (nullptr != pidlFolder) ? L"<present>" : L"<null>",
      static_cast<unsigned int>(cidl),
      static_cast<unsigned int>(dwFlags));

  if (nullptr != pidlFolder)
  {
    // A shell identifier that does not refer to a file system location (a virtual folder, for
    // example) has no path to redirect and is passed through untouched.
    wchar_t folderPath[MAX_PATH];
    const DWORD lastError = GetLastError();

    if (FALSE != SHGetPathFromIDListW(pidlFolder, folderPath))
    {
      originalFolderPath.assign(folderPath);
      folderReplacement = LookupRedirectedTargetPath(originalFolderPath);
    }

    SetLastError(lastError);
  }

  if (false == folderReplacement.has_value())
  {
    return Original(pidlFolder, cidl, apidl, dwFlags);
  }

  // Child identifiers are relative to the folder, and redirection rules preserve the directory
  // hierarchy, so the children keep resolving correctly inside the redirected folder.
  PIDLIST_ABSOLUTE redirectedPidlFolder = ILCreateFromPathW(folderReplacement->c_str());

  if (nullptr == redirectedPidlFolder)
  {
    return Original(pidlFolder, cidl, apidl, dwFlags);
  }

  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s: Redirecting shell folder from \"%s\" to \"%s\".",
      GetFunctionName(),
      originalFolderPath.c_str(),
      folderReplacement->c_str());

  const HRESULT result = Original(redirectedPidlFolder, cidl, apidl, dwFlags);

  ILFree(redirectedPidlFolder);

  return result;
}

HRESULT Pathwinder::Hooks::DynamicHook_SHParseDisplayName::Hook(
    PCWSTR pszName, IBindCtx* pbc, PIDLIST_ABSOLUTE* ppidl, SFGAOF sfgaoIn, SFGAOF* psfgaoOut)
{
  if (nullptr == pszName) return Original(pszName, pbc, ppidl, sfgaoIn, psfgaoOut);

  // The identifier produced here is consumed by Explorer and by any application the shell starts,
  // neither of which is redirected, so it has to refer to the redirected location.
  std::optional<std::wstring> pathReplacement = LookupRedirectedTargetPath(pszName);

  if (true == pathReplacement.has_value())
  {
    Infra::Message::OutputFormatted(
        Infra::Message::ESeverity::Info,
        L"%s: Redirecting shell namespace path from \"%s\" to \"%s\".",
        GetFunctionName(),
        pszName,
        pathReplacement->c_str());

    pszName = pathReplacement->c_str();
  }

  const HRESULT result = Original(pszName, pbc, ppidl, sfgaoIn, psfgaoOut);

  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s: Parsed \"%s\" with bind context %s, result 0x%08X.",
      GetFunctionName(),
      pszName,
      (nullptr != pbc) ? L"present" : L"null",
      static_cast<unsigned int>(result));

  // A name that is not a full path is resolved by the shell against the folder carried by the bind
  // context, so the identifier can end up referring to an origin-drive location even though the
  // name itself was never a path. The identifier is what leaves this process, so it has to be
  // re-created from the redirected location to keep both sides of the handoff on the same file.
  if ((S_OK == result) && (nullptr != ppidl) && (nullptr != *ppidl))
  {
    wchar_t parsedPathBuffer[1024];

    if (FALSE != SHGetPathFromIDListW(*ppidl, parsedPathBuffer))
    {
      std::optional<std::wstring> parsedPathReplacement =
          LookupRedirectedTargetPath(parsedPathBuffer);

      if (true == parsedPathReplacement.has_value())
      {
        Infra::Message::OutputFormatted(
            Infra::Message::ESeverity::Info,
            L"%s: Redirecting parsed shell identifier from \"%s\" to \"%s\".",
            GetFunctionName(),
            parsedPathBuffer,
            parsedPathReplacement->c_str());

        ILFree(*ppidl);
        *ppidl = nullptr;

        return Original(parsedPathReplacement->c_str(), nullptr, ppidl, sfgaoIn, psfgaoOut);
      }
    }
  }

  return result;
}

PIDLIST_ABSOLUTE Pathwinder::Hooks::DynamicHook_ILCreateFromPathW::Hook(PCWSTR pszPath)
{
  if (nullptr == pszPath) return Original(pszPath);

  // Diagnostic: record every identifier creation so the exact path can be inspected.
  Infra::Message::OutputFormatted(
      Infra::Message::ESeverity::Info,
      L"%s: Entry with path \"%s\".",
      GetFunctionName(),
      pszPath);

  // Same boundary as SHParseDisplayName: the identifier leaves this process and is interpreted by
  // a component that sees the real filesystem.
  std::optional<std::wstring> pathReplacement = LookupRedirectedTargetPath(pszPath);

  if (true == pathReplacement.has_value())
  {
    Infra::Message::OutputFormatted(
        Infra::Message::ESeverity::Info,
        L"%s: Redirecting shell identifier path from \"%s\" to \"%s\".",
        GetFunctionName(),
        pszPath,
        pathReplacement->c_str());

    pszPath = pathReplacement->c_str();
  }

  return Original(pszPath);
}

/// Determines if the specified string is an absolute drive-letter path, which is the only form
/// that takes part in redirection.
/// @param [in] path Path to check.
/// @return `true` if the path begins with a drive letter and a directory separator, `false`
/// otherwise.
static bool IsAbsoluteDriveLetterPath(std::wstring_view path)
{
  return ((path.size() >= 3) && (0 != iswalpha(path[0])) && (L':' == path[1]) &&
          ((L'\\' == path[2]) || (L'/' == path[2])));
}

/// Builds a string that removes the Windows namespace prefix that internally associated paths
/// carry, leaving a plain absolute path.
/// @param [in] path Path to convert, possibly prefixed.
/// @return Path without a Windows namespace prefix.
static std::wstring_view StripWindowsNamespacePrefix(std::wstring_view path)
{
  path.remove_prefix(Pathwinder::Strings::PathGetWindowsNamespacePrefix(path).length());
  return path;
}

DWORD Pathwinder::Hooks::DynamicHook_GetFinalPathNameByHandleW::Hook(
    HANDLE hFile, LPWSTR lpszFilePath, DWORD cchFilePath, DWORD dwFlags)
{
  const DWORD callResult = Original(hFile, lpszFilePath, cchFilePath, dwFlags);

  // Handles opened through the file hooks are stored together with both the path that the
  // application asked for and the path that was actually opened. Only the second one leaking
  // out of this process is a problem, so handles without a difference are passed through.
  const std::optional<Pathwinder::OpenHandleStore::SHandleDataView> handleData =
      OpenHandleStoreInstance().GetDataForHandle(hFile);

  if (false == handleData.has_value()) return callResult;
  if (handleData->associatedPath == handleData->realOpenedPath) return callResult;

  const std::wstring_view associatedPath =
      StripWindowsNamespacePrefix(handleData->associatedPath);

  // The requested form is part of the result, and only the DOS form can be produced from the
  // associated path alone: a volume identifier or an NT device name would require information
  // about the origin volume that is not available here.
  if (0 != (dwFlags & (VOLUME_NAME_GUID | VOLUME_NAME_NT))) return callResult;

  std::wstring replacement;

  if (0 != (dwFlags & VOLUME_NAME_NONE))
  {
    // Without a volume name the result starts at the root directory, so the drive letter and
    // the colon are dropped and the separator is retained.
    replacement.assign(associatedPath.substr(2));
  }
  else
  {
    // The DOS form carries the Windows namespace prefix, which is what the associated path
    // already uses internally, just written differently.
    replacement.append(L"\\\\?\\");
    replacement.append(associatedPath.substr(0, 2));
    replacement.append(associatedPath.substr(2));
  }

  // The caller is expected to retry with a larger buffer, whose required size is reported here.
  if ((nullptr == lpszFilePath) || (cchFilePath <= replacement.length()))
    return static_cast<DWORD>(replacement.length() + 1);

  wmemcpy(lpszFilePath, replacement.data(), replacement.length());
  lpszFilePath[replacement.length()] = L'\0';

  return static_cast<DWORD>(replacement.length());
}

BOOL Pathwinder::Hooks::DynamicHook_GetVolumePathNameW::Hook(
    LPCWSTR lpszFileName, LPWSTR lpszVolumePathName, DWORD cchBufferLength)
{
  const DWORD callerLastError = GetLastError();

  const BOOL callResult = Original(lpszFileName, lpszVolumePathName, cchBufferLength);
  const DWORD callLastError = GetLastError();

  if ((nullptr == lpszFileName) || (false == IsAbsoluteDriveLetterPath(lpszFileName)))
  {
    SetLastError(callLastError);
    return callResult;
  }

  const std::optional<std::wstring> redirectedPath = LookupRedirectedTargetPath(lpszFileName);

  if (false == redirectedPath.has_value())
  {
    SetLastError(callLastError);
    return callResult;
  }

  // The file lives on the origin drive as far as this process is concerned, so the volume that
  // contains it is the origin volume and not the one that actually stores the data.
  std::wstring originVolumeRoot(lpszFileName, 3);

  if (L'/' == originVolumeRoot[2]) originVolumeRoot[2] = L'\\';

  if ((nullptr == lpszVolumePathName) || (cchBufferLength <= originVolumeRoot.length()))
  {
    SetLastError(ERROR_INSUFFICIENT_BUFFER);
    return FALSE;
  }

  wmemcpy(lpszVolumePathName, originVolumeRoot.data(), originVolumeRoot.length());
  lpszVolumePathName[originVolumeRoot.length()] = L'\0';

  SetLastError(callerLastError);

  return TRUE;
}
