/***************************************************************************************************
 * Pathwinder
 *   Path redirection for files, directories, and registry entries.
 ***************************************************************************************************
 * @file ExtInternal.h
 *   Accessors that allow extension code to reach the process-wide state owned by the upstream
 *   hook module implementation. Only the fork's own extension files should use these.
 ***********************************************************************************************/

#pragma once

#include "FilesystemDirector.h"
#include "OpenHandleStore.h"

namespace Pathwinder
{
  namespace Hooks
  {
    namespace Ext
    {
      /// Retrieves a reference to the filesystem director object instance used by all hooks.
      /// @return Mutable reference to the filesystem director object instance.
      FilesystemDirector& FilesystemDirectorInstance(void);

      /// Retrieves a reference to the open handle store instance used by all hooks.
      /// @return Mutable reference to the open handle store instance.
      OpenHandleStore& OpenHandleStoreInstance(void);

      /// Retrieves a new identifier for a particular invocation of a hook function.
      /// Used exclusively for logging.
      /// @return Numeric identifier for an invocation.
      unsigned int GetRequestIdentifier(void);
    } // namespace Ext
  } // namespace Hooks
} // namespace Pathwinder
