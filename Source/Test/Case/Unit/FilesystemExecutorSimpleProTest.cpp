/***************************************************************************************************
 * Pathwinder
 *   Path redirection for files, directories, and registry entries.
 ***************************************************************************************************
 * @file FilesystemExecutorSimpleProTest.cpp
 *   Unit tests for simple-mode-with-read-fallback (Simple Pro) behavior added by this fork. They
 *   live here rather than in FilesystemExecutorTest.cpp so that the upstream test file stays
 *   untouched and upstream changes merge cleanly.
 **************************************************************************************************/

#include "FilesystemExecutor.h"

#include <string_view>

#include <Infra/Test/TestCase.h>

#include "ApiWindows.h"
#include "ExtFilesystem.h"
#include "FilesystemDirector.h"
#include "FilesystemInstruction.h"
#include "FilesystemRule.h"
#include "MockFilesystemOperations.h"
#include "OpenHandleStore.h"
#include "Strings.h"

namespace PathwinderTest
{
  using namespace ::Pathwinder;

  /// Function request identifier to be passed to all filesystem executor functions when they are
  /// invoked for testing.
  static constexpr unsigned int kFunctionRequestIdentifier = 0;

  /// Creates and returns an object attributes structure for the specified filename and optional
  /// root directory handle.
  /// @param [in] fileName System Unicode string representation of the filename to initialize inside
  /// the object attributes structure.
  /// @param [in] rootDirectory Handle for the root directory. Defaults to no root directory. If
  /// specified, the file name is relative to this open directory.
  /// @return Initialized object attributes data structure.
  static inline OBJECT_ATTRIBUTES CreateObjectAttributes(
      UNICODE_STRING& fileName, HANDLE rootDirectory = NULL)
  {
    return {
        .Length = sizeof(OBJECT_ATTRIBUTES),
        .RootDirectory = rootDirectory,
        .ObjectName = &fileName};
  }

  // Verifies the conditions under which a simple-mode-with-read-fallback (Simple Pro) instruction
  // retries a failed open using the unredirected filename. Plain simple-mode instructions never
  // fall back, directories are never opened through the fallback, only not-found style failures
  // trigger the fallback, and both write and delete access still fall back because a fallback only
  // happens for files that live outside the redirected location but still need to be operated on.
  TEST_CASE(FilesystemExecutor_NewFileHandle_UnredirectedFallbackConditions)
  {
    constexpr std::wstring_view kUnredirectedPath = L"C:\\TestDirectory\\TestFile.txt";
    constexpr std::wstring_view kRedirectedPath = L"C:\\RedirectedDirectory\\TestFile.txt";

    UNICODE_STRING unicodeStringUnredirectedPath =
        Strings::NtConvertStringViewToUnicodeString(kUnredirectedPath);
    OBJECT_ATTRIBUTES objectAttributesUnredirectedPath =
        CreateObjectAttributes(unicodeStringUnredirectedPath);

    const struct
    {
      bool useSimpleProInstruction;
      ACCESS_MASK desiredAccess;
      ULONG createDisposition;
      ULONG createOptions;
      NTSTATUS forwardResult;
      unsigned int expectedNumSystemCallInvocations;
      NTSTATUS expectedReturnCode;
    } fallbackTestRecords[] = {
        // Plain simple mode never falls back.
        {.useSimpleProInstruction = false,
         .desiredAccess = GENERIC_READ,
         .createDisposition = FILE_OPEN,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 1,
         .expectedReturnCode = NtStatus::kObjectPathNotFound},
        // Ordinary open of an existing file falls back.
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_READ,
         .createDisposition = FILE_OPEN,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 2,
         .expectedReturnCode = NtStatus::kSuccess},
        // Write access still falls back.
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_READ | GENERIC_WRITE,
         .createDisposition = FILE_OPEN,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 2,
         .expectedReturnCode = NtStatus::kSuccess},
        // Delete access still falls back.
        {.useSimpleProInstruction = true,
         .desiredAccess = DELETE,
         .createDisposition = FILE_OPEN,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 2,
         .expectedReturnCode = NtStatus::kSuccess},
        // Directory opens do not fall back.
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_READ,
         .createDisposition = FILE_OPEN,
         .createOptions = FILE_DIRECTORY_FILE,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 1,
         .expectedReturnCode = NtStatus::kObjectPathNotFound},
        // Create-like dispositions do not fall back.
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_READ | GENERIC_WRITE,
         .createDisposition = FILE_CREATE,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 1,
         .expectedReturnCode = NtStatus::kObjectPathNotFound},
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_READ | GENERIC_WRITE,
         .createDisposition = FILE_OVERWRITE_IF,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 1,
         .expectedReturnCode = NtStatus::kObjectPathNotFound},
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_WRITE,
         .createDisposition = FILE_SUPERSEDE,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 1,
         .expectedReturnCode = NtStatus::kObjectPathNotFound},
        // Open-or-create does not fall back.
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_READ | GENERIC_WRITE,
         .createDisposition = FILE_OPEN_IF,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectPathNotFound,
         .expectedNumSystemCallInvocations = 1,
         .expectedReturnCode = NtStatus::kObjectPathNotFound},
        // Only not-found style failures trigger the fallback.
        {.useSimpleProInstruction = true,
         .desiredAccess = GENERIC_READ,
         .createDisposition = FILE_OPEN,
         .createOptions = 0,
         .forwardResult = NtStatus::kObjectTypeMismatch,
         .expectedNumSystemCallInvocations = 1,
         .expectedReturnCode = NtStatus::kObjectTypeMismatch},
    };

    for (const auto& fallbackTestRecord : fallbackTestRecords)
    {
      HANDLE unusedHandleValue = NULL;

      MockFilesystemOperations mockFilesystem;
      OpenHandleStore openHandleStore;

      const FileOperationInstruction testInputFileOperationInstruction =
          (fallbackTestRecord.useSimpleProInstruction
               ? Ext::SimpleProRedirectTo(kRedirectedPath)
               : FileOperationInstruction::SimpleRedirectTo(kRedirectedPath));

      unsigned int underlyingSystemCallNumInvocations = 0;

      const NTSTATUS actualReturnCode = FilesystemExecutor::NewFileHandle(
          TestCaseName().data(),
          kFunctionRequestIdentifier,
          openHandleStore,
          &unusedHandleValue,
          fallbackTestRecord.desiredAccess,
          &objectAttributesUnredirectedPath,
          0,
          fallbackTestRecord.createDisposition,
          fallbackTestRecord.createOptions,
          [&testInputFileOperationInstruction](
              std::wstring_view, FileAccessMode, CreateDisposition) -> FileOperationInstruction
          {
            return testInputFileOperationInstruction;
          },
          [&underlyingSystemCallNumInvocations, &fallbackTestRecord](
              PHANDLE, POBJECT_ATTRIBUTES objectAttributes, ULONG) -> NTSTATUS
          {
            std::wstring_view expectedPathToTry =
                ((0 == underlyingSystemCallNumInvocations) ? kRedirectedPath : kUnredirectedPath);
            std::wstring_view actualPathToTry =
                Strings::NtConvertUnicodeStringToStringView(*objectAttributes->ObjectName);
            TEST_ASSERT(actualPathToTry == expectedPathToTry);

            underlyingSystemCallNumInvocations += 1;

            // The forward attempt reports whatever the test record dictates. Any subsequent
            // attempt is the unredirected fallback and succeeds.
            return (
                (1 == underlyingSystemCallNumInvocations) ? fallbackTestRecord.forwardResult
                                                          : NtStatus::kSuccess);
          });

      TEST_ASSERT(actualReturnCode == fallbackTestRecord.expectedReturnCode);
      TEST_ASSERT(
          underlyingSystemCallNumInvocations ==
          fallbackTestRecord.expectedNumSystemCallInvocations);
      TEST_ASSERT(true == openHandleStore.Empty());
    }
  }

  // Verifies that a query issued with delete access still falls back to the unredirected filename
  // in simple-mode-with-read-fallback mode. `NtDeleteFile` reaches this function with `DELETE`
  // access, so this exercises that path without any extra access restrictions.
  TEST_CASE(FilesystemExecutor_QueryByObjectAttributes_UnredirectedFallbackWithDeleteAccess)
  {
    constexpr std::wstring_view kUnredirectedPath = L"C:\\TestDirectory\\TestFile.txt";
    constexpr std::wstring_view kRedirectedPath = L"C:\\RedirectedDirectory\\TestFile.txt";

    UNICODE_STRING unicodeStringUnredirectedPath =
        Strings::NtConvertStringViewToUnicodeString(kUnredirectedPath);
    OBJECT_ATTRIBUTES objectAttributesUnredirectedPath =
        CreateObjectAttributes(unicodeStringUnredirectedPath);

    MockFilesystemOperations mockFilesystem;
    OpenHandleStore openHandleStore;

    const FileOperationInstruction testInputFileOperationInstruction =
        Ext::SimpleProRedirectTo(kRedirectedPath);

    unsigned int underlyingSystemCallNumInvocations = 0;

    const NTSTATUS actualReturnCode = FilesystemExecutor::QueryByObjectAttributes(
        TestCaseName().data(),
        kFunctionRequestIdentifier,
        openHandleStore,
        &objectAttributesUnredirectedPath,
        DELETE,
        [&testInputFileOperationInstruction](
            std::wstring_view, FileAccessMode, CreateDisposition) -> FileOperationInstruction
        {
          return testInputFileOperationInstruction;
        },
        [&underlyingSystemCallNumInvocations](POBJECT_ATTRIBUTES objectAttributes) -> NTSTATUS
        {
          std::wstring_view expectedPathToTry =
              ((0 == underlyingSystemCallNumInvocations) ? kRedirectedPath : kUnredirectedPath);
          std::wstring_view actualPathToTry =
              Strings::NtConvertUnicodeStringToStringView(*objectAttributes->ObjectName);
          TEST_ASSERT(actualPathToTry == expectedPathToTry);

          underlyingSystemCallNumInvocations += 1;

          return (
              (1 == underlyingSystemCallNumInvocations) ? NtStatus::kObjectPathNotFound
                                                        : NtStatus::kSuccess);
        });

    TEST_ASSERT(actualReturnCode == NtStatus::kSuccess);
    TEST_ASSERT(2 == underlyingSystemCallNumInvocations);
    TEST_ASSERT(true == openHandleStore.Empty());
  }

} // namespace PathwinderTest
