#pragma once

#include <credentialprovider.h>

namespace CimianStatus {

// Field ids for our single tile. Indices must match the entries in g_FieldDescriptors.
enum FieldId : DWORD {
    FIELD_TILE_IMAGE      = 0,  // Cimian logo
    FIELD_LARGE_TEXT      = 1,  // Status line, e.g. "Installing Microsoft Office"
    FIELD_SMALL_TEXT      = 2,  // Detail line
    FIELD_PROGRESS_IMAGE  = 3,  // Bitmap-rendered progress bar
    FIELD_PERCENT_TEXT    = 4,  // "47%"
    FIELD_VIEW_LOG_LINK   = 5,  // Command link toggling the log field
    FIELD_LOG_TEXT        = 6,  // Tail of managedsoftwareupdate.log (toggled by FIELD_VIEW_LOG_LINK)
    FIELD_COUNT           = 7
};

// Static descriptors registered with LogonUI. The pointer values are duplicated
// at runtime via SHStrDupW so LogonUI can free them with CoTaskMemFree.
extern const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR g_FieldDescriptors[FIELD_COUNT];

// Initial visibility/interactivity state per field.
extern const CREDENTIAL_PROVIDER_FIELD_STATE       g_FieldState[FIELD_COUNT];
extern const CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE g_FieldInteractiveState[FIELD_COUNT];

// Default string values used when no status update has arrived yet.
extern const wchar_t* const g_FieldInitialStrings[FIELD_COUNT];

} // namespace CimianStatus
