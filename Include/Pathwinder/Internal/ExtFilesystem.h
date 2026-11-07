/***************************************************************************************************
 * Pathwinder
 *   Path redirection for files, directories, and registry entries.
 ***************************************************************************************************
 * @file ExtFilesystem.h
 *   Redirection helpers added by this fork. These live outside of the upstream headers so that
 *   upstream changes merge with minimal conflicts.
 ***********************************************************************************************/

#pragma once

#include <functional>
#include <optional>
#include <string>
#include <string_view>
#include <utility>

#include <Infra/Core/Message.h>

#include "ApiWindows.h"
#include "FilesystemInstruction.h"
#include "Strings.h"

namespace Pathwinder
{
  namespace Ext
  {
    /// Creates a filesystem operation redirection instruction that indicates the request should
    /// be redirected in simple mode with read fallback. Only the redirected file is tried, but
    /// if the operation fails because the redirected file does not exist then the unredirected
    /// file may be tried once as a fallback.
    /// @param [in] redirectedFilename String representing the absolute redirected filename,
    /// including Windows namespace prefix.
    /// @param [in] filenameHandleAssociation How to associate a filename with a potentially
    /// newly-created filesystem handle. Optional, defaults to no association.
    /// @param [in] extraPreOperations Any extra pre-operations to be performed before the file
    /// operation is attempted. Optional, defaults to none.
    /// @param [in] extraPreOperationOperand Additional operand for any extra pre-operations.
    /// Optional, defaults to none.
    /// @return File operation redirection instruction encoded to indicate redirection plus
    /// optionally some additional processing.
    inline FileOperationInstruction SimpleProRedirectTo(
        Infra::TemporaryString&& redirectedFilename,
        EAssociateNameWithHandle filenameHandleAssociation = EAssociateNameWithHandle::None,
        BitSetEnum<EExtraPreOperation>&& extraPreOperations = {},
        std::wstring_view extraPreOperationOperand = std::wstring_view())
    {
      return FileOperationInstruction(
          std::move(redirectedFilename),
          ETryFiles::RedirectedOnlyWithFallback,
          ECreateDispositionPreference::NoPreference,
          filenameHandleAssociation,
          std::move(extraPreOperations),
          extraPreOperationOperand);
    }

    /// Determines whether a failed file operation should be retried using the unredirected
    /// filename. This fallback applies only to simple-mode-with-read-fallback (Simple Pro)
    /// instructions (which otherwise try only the redirected filename) when the redirected file
    /// could not be found.
    /// @param [in] instruction Instruction that specifies how to redirect a filesystem operation.
    /// @param [in] systemCallResult Result of the file operation attempt that used the redirected
    /// filename.
    /// @return `true` if the operation should be retried using the unredirected filename.
    inline bool ShouldTryUnredirectedFilenameFallback(
        const FileOperationInstruction& instruction, NTSTATUS systemCallResult)
    {
      switch (systemCallResult)
      {
        case NtStatus::kObjectNameNotFound:
        case NtStatus::kObjectPathNotFound:
        case NtStatus::kNoSuchFile:
          break;

        default:
          return false;
      }

      return (
          instruction.HasRedirectedFilename() &&
          (ETryFiles::RedirectedOnlyWithFallback == instruction.GetFilenamesToTry()));
    }

    /// Attempts the unredirected fallback for a Simple Pro instruction.
    /// Returns no value when the fallback is not applicable or when it failed; otherwise returns
    /// the fallback attempt's result code.
    /// @param [in] functionName Name of the API function whose hook function is invoking this
    /// function. Used only for logging.
    /// @param [in] functionRequestIdentifier Request identifier associated with the invocation of
    /// the named function. Used only for logging.
    /// @param [in] instruction Instruction that specifies how to redirect the operation.
    /// @param [in] objectAttributesFromApp Object attributes structure received from the
    /// application.
    /// @param [in] composedUnredirectedPath Absolute unredirected path composed from a root
    /// directory handle, or an empty view if no composition was needed.
    /// @param [in] forwardResult Result of the attempt that used the redirected filename.
    /// @param [in] actionDescription Verb describing the operation, used only for logging.
    /// @param [in] fallbackInvoker Function that submits the fallback attempt to the system.
    /// @return Result of the fallback attempt, or no value if no fallback was performed.
    inline std::optional<NTSTATUS> TryUnredirectedFilenameFallback(
        const wchar_t* functionName,
        unsigned int functionRequestIdentifier,
        const FileOperationInstruction& instruction,
        const OBJECT_ATTRIBUTES& objectAttributesFromApp,
        std::wstring_view composedUnredirectedPath,
        NTSTATUS forwardResult,
        std::wstring_view actionDescription,
        const std::function<NTSTATUS(POBJECT_ATTRIBUTES)>& fallbackInvoker)
    {
      if (false == ShouldTryUnredirectedFilenameFallback(instruction, forwardResult))
        return std::nullopt;

      // The unredirected name has to be built in place because the `OBJECT_ATTRIBUTES` structure
      // refers to its `ObjectName` field by pointer.
      UNICODE_STRING unredirectedObjectName = {};
      OBJECT_ATTRIBUTES unredirectedObjectAttributes = objectAttributesFromApp;

      if (false == composedUnredirectedPath.empty())
      {
        unredirectedObjectName =
            Strings::NtConvertStringViewToUnicodeString(composedUnredirectedPath);
        unredirectedObjectAttributes.RootDirectory = nullptr;
        unredirectedObjectAttributes.ObjectName = &unredirectedObjectName;
      }

      const NTSTATUS fallbackResult = fallbackInvoker(&unredirectedObjectAttributes);
      if (false == NT_SUCCESS(fallbackResult)) return std::nullopt;

      std::wstring_view redirectedPath = instruction.GetRedirectedFilename();
      Infra::Message::OutputFormatted(
          Infra::Message::ESeverity::Info,
          L"%s(%u): Redirected file \"%.*s\" does not exist; %.*s unredirected file instead.",
          functionName,
          functionRequestIdentifier,
          static_cast<int>(redirectedPath.length()),
          redirectedPath.data(),
          static_cast<int>(actionDescription.length()),
          actionDescription.data());

      return fallbackResult;
    }
  } // namespace Ext
} // namespace Pathwinder
