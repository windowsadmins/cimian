// Renders a progress bar to an HBITMAP using GDI. Kept GDI-only (no GDI+) so
// the DLL stays small and dependency-free in LogonUI's process.
#pragma once

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

namespace CimianStatus {

// Render an N% progress bar to a 256x16 32-bit DIB. Returns an HBITMAP that the
// caller takes ownership of (DeleteObject when done). Returns nullptr on
// failure. percent is clamped to [0, 100].
HBITMAP RenderProgressBitmap(int percent);

} // namespace CimianStatus
