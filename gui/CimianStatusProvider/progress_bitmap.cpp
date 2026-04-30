#include "progress_bitmap.h"

namespace CimianStatus {

namespace {
constexpr int kWidth  = 256;
constexpr int kHeight = 16;
}

HBITMAP RenderProgressBitmap(int percent) {
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;

    BITMAPINFO bmi{};
    bmi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
    bmi.bmiHeader.biWidth       = kWidth;
    bmi.bmiHeader.biHeight      = -kHeight; // top-down
    bmi.bmiHeader.biPlanes      = 1;
    bmi.bmiHeader.biBitCount    = 32;
    bmi.bmiHeader.biCompression = BI_RGB;

    void* pixelData = nullptr;
    HBITMAP bmp = CreateDIBSection(nullptr, &bmi, DIB_RGB_COLORS, &pixelData, nullptr, 0);
    if (!bmp || !pixelData) return nullptr;

    HDC screenDC = GetDC(nullptr);
    HDC memDC    = CreateCompatibleDC(screenDC);
    HGDIOBJ oldBmp = SelectObject(memDC, bmp);

    // Background — Cimian-ish dark slate so it reads on the default Windows
    // logon backdrop. RGB(40, 44, 52).
    HBRUSH bgBrush = CreateSolidBrush(RGB(40, 44, 52));
    RECT all = { 0, 0, kWidth, kHeight };
    FillRect(memDC, &all, bgBrush);
    DeleteObject(bgBrush);

    // Border.
    HPEN borderPen = CreatePen(PS_SOLID, 1, RGB(70, 78, 90));
    HGDIOBJ oldPen = SelectObject(memDC, borderPen);
    HBRUSH nullBrush = (HBRUSH)GetStockObject(NULL_BRUSH);
    HGDIOBJ oldBrush = SelectObject(memDC, nullBrush);
    Rectangle(memDC, 0, 0, kWidth, kHeight);
    SelectObject(memDC, oldPen);
    SelectObject(memDC, oldBrush);
    DeleteObject(borderPen);

    // Fill bar — Cimian accent (Munki-ish blue). RGB(48, 140, 220).
    int fillW = (kWidth - 2) * percent / 100;
    if (fillW > 0) {
        RECT fill = { 1, 1, 1 + fillW, kHeight - 1 };
        HBRUSH fillBrush = CreateSolidBrush(RGB(48, 140, 220));
        FillRect(memDC, &fill, fillBrush);
        DeleteObject(fillBrush);
    }

    SelectObject(memDC, oldBmp);
    DeleteDC(memDC);
    ReleaseDC(nullptr, screenDC);

    return bmp;
}

} // namespace CimianStatus
