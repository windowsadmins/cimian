#include "fields.h"

namespace CimianStatus {

// Mutable casts: LogonUI receives a copy of the descriptor, so we don't need
// CoTaskMemAlloc-backed strings here — but the pointers must remain valid for
// the lifetime of the descriptor array we hand back. Static storage suffices.
static wchar_t s_LabelLargeText[]    = L"Status";
static wchar_t s_LabelSmallText[]    = L"Detail";
static wchar_t s_LabelProgress[]     = L"Progress";
static wchar_t s_LabelPercentText[]  = L"Percent";
static wchar_t s_LabelViewLog[]      = L"View log";
static wchar_t s_LabelLogText[]      = L"Log";
static wchar_t s_LabelTileImage[]    = L"Cimian";

const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR g_FieldDescriptors[FIELD_COUNT] = {
    { FIELD_TILE_IMAGE,     CPFT_TILE_IMAGE,    s_LabelTileImage   },
    { FIELD_LARGE_TEXT,     CPFT_LARGE_TEXT,    s_LabelLargeText   },
    { FIELD_SMALL_TEXT,     CPFT_SMALL_TEXT,    s_LabelSmallText   },
    { FIELD_PROGRESS_IMAGE, CPFT_TILE_IMAGE,    s_LabelProgress    },
    { FIELD_PERCENT_TEXT,   CPFT_SMALL_TEXT,    s_LabelPercentText },
    { FIELD_VIEW_LOG_LINK,  CPFT_COMMAND_LINK,  s_LabelViewLog     },
    { FIELD_LOG_TEXT,       CPFT_LARGE_TEXT,    s_LabelLogText     },
};

// Status, detail, percent and progress are visible whether the tile is selected
// or not. The "View log" link and the log body are visible only when the tile
// is expanded (selected) so the deselected pill stays compact.
const CREDENTIAL_PROVIDER_FIELD_STATE g_FieldState[FIELD_COUNT] = {
    CPFS_DISPLAY_IN_BOTH,              // FIELD_TILE_IMAGE
    CPFS_DISPLAY_IN_BOTH,              // FIELD_LARGE_TEXT
    CPFS_DISPLAY_IN_SELECTED_TILE,     // FIELD_SMALL_TEXT
    CPFS_DISPLAY_IN_BOTH,              // FIELD_PROGRESS_IMAGE
    CPFS_DISPLAY_IN_BOTH,              // FIELD_PERCENT_TEXT
    CPFS_DISPLAY_IN_SELECTED_TILE,     // FIELD_VIEW_LOG_LINK
    CPFS_HIDDEN,                       // FIELD_LOG_TEXT — toggled by command link
};

const CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE g_FieldInteractiveState[FIELD_COUNT] = {
    CPFIS_NONE,    // FIELD_TILE_IMAGE
    CPFIS_NONE,    // FIELD_LARGE_TEXT
    CPFIS_NONE,    // FIELD_SMALL_TEXT
    CPFIS_NONE,    // FIELD_PROGRESS_IMAGE
    CPFIS_NONE,    // FIELD_PERCENT_TEXT
    CPFIS_NONE,    // FIELD_VIEW_LOG_LINK
    CPFIS_NONE,    // FIELD_LOG_TEXT
};

const wchar_t* const g_FieldInitialStrings[FIELD_COUNT] = {
    nullptr,                                  // tile image — set via SetFieldBitmap
    L"Cimian is preparing this device",       // large text
    L"Waiting for managedsoftwareupdate...",  // small text
    nullptr,                                  // progress image — rendered on first percent
    L"",                                      // percent text
    L"View log",                              // command link
    L"",                                      // log text body
};

} // namespace CimianStatus
