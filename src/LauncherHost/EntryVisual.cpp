#include "EntryVisual.h"

#include "TaskbarGeometry.h"

#include <algorithm>
#include <cmath>
#include <vector>

namespace winwidgetboard::launcher
{
namespace
{
// The panel's open/close spring, restated so the entry settles on the same curve.
// See PanelMotionController in the WorkspacePanel project.
constexpr double kSpringAngularFrequency = 15.491933384829668;
constexpr double kReducedMotionTimeConstant = 0.045;
constexpr double kSettleValueEpsilon = 0.002;
constexpr double kSettleVelocityEpsilon = 0.02;
// Within the 0.96-0.98 press range the UI spec allows.
constexpr double kPressScale = 0.97;
// A single soft highlight along the top edge reads as a lit glass rim. Anything stronger
// turns into the per-container outline the visual spec forbids.
constexpr double kTopHighlightAlpha = 0.16;
constexpr double kFillGradientAmount = 0.22;
constexpr double kIndicatorRestAlpha = 0.55;
// The segment separator, relative to the text it sits between.
constexpr double kDividerAlpha = 0.30;

// --- condition illustration palette ----------------------------------------------------
//
// The weather card paints a flat condition illustration behind its content
// (WeatherBackdrop*Template in MainWindow.xaml, coloured by the WwbWeather* brushes in
// WorkspaceVisualStyles.xaml). The entry carries the same artwork, but it has no XAML and so
// no ThemeResource lookup; the palette is restated here the way the 4 DIP metrics above
// already are. WorkspaceVisualStyles.xaml stays the authority - these are its Default
// dictionary for a dark strip and its Light dictionary for a light one, and high contrast
// draws no illustration at all because every WwbWeather* brush is Transparent there.
//
// The motif is composed in a square box this many capsule heights across, anchored just past
// the closing cap so it bleeds a little - the entry's equivalent of the card's negative
// margins. It stays close to one capsule height on purpose: a larger box turns the artwork
// into a cropped blob at this size instead of a drawing.
constexpr double kMotifUnitScale = 1.05;
constexpr double kMotifOverflowX = 0.02;
constexpr double kMotifOverflowY = 0.05;

constexpr wchar_t kPersonalizeKey[] =
    L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
constexpr wchar_t kSystemUsesLightThemeValue[] = L"SystemUsesLightTheme";

int ScaleLogical(const int logicalPixels, const UINT dpi)
{
    const UINT effectiveDpi = dpi == 0 ? 96 : dpi;
    const int scaled = MulDiv(logicalPixels, static_cast<int>(effectiveDpi), 96);
    return scaled <= 0 ? 1 : scaled;
}

int ToLogical(const int physicalPixels, const UINT dpi)
{
    const UINT effectiveDpi = dpi == 0 ? 96 : dpi;
    const int logical = MulDiv(physicalPixels, 96, static_cast<int>(effectiveDpi));
    return logical <= 0 ? 1 : logical;
}

double Clamp01(const double value)
{
    return std::clamp(value, 0.0, 1.0);
}

double Lerp(const double from, const double to, const double amount)
{
    return from + (to - from) * amount;
}

COLORREF MixColor(const COLORREF from, const COLORREF to, const double amount)
{
    const double blend = Clamp01(amount);
    const auto channel = [blend](const int a, const int b)
    {
        return static_cast<int>(std::lround(Lerp(
            static_cast<double>(a),
            static_cast<double>(b),
            blend)));
    };

    return RGB(
        channel(GetRValue(from), GetRValue(to)),
        channel(GetGValue(from), GetGValue(to)),
        channel(GetBValue(from), GetBValue(to)));
}

double RectWidthOf(const RECT& rectangle)
{
    return static_cast<double>(rectangle.right - rectangle.left);
}

double RectHeightOf(const RECT& rectangle)
{
    return static_cast<double>(rectangle.bottom - rectangle.top);
}

// Signed distance to a rounded rectangle. Negative inside, positive outside, and continuous
// across the boundary - which is what makes a one-pixel coverage band produce clean
// antialiasing without supersampling.
double RoundedRectDistance(
    const double x,
    const double y,
    const double centerX,
    const double centerY,
    const double halfWidth,
    const double halfHeight,
    const double radius)
{
    const double effectiveRadius =
        std::min(radius, std::min(halfWidth, halfHeight));
    const double dx = std::abs(x - centerX) - (halfWidth - effectiveRadius);
    const double dy = std::abs(y - centerY) - (halfHeight - effectiveRadius);
    const double outsideX = std::max(dx, 0.0);
    const double outsideY = std::max(dy, 0.0);
    const double outside = std::sqrt(outsideX * outsideX + outsideY * outsideY);
    const double inside = std::min(std::max(dx, dy), 0.0);
    return outside + inside - effectiveRadius;
}

double CoverageFromDistance(const double distance)
{
    return Clamp01(0.5 - distance);
}

struct Canvas
{
    BYTE* bits{};
    int width{};
    int height{};

    [[nodiscard]] bool Contains(const int x, const int y) const noexcept
    {
        return x >= 0 && y >= 0 && x < width && y < height;
    }

    // Source-over composite. The buffer is premultiplied, as UpdateLayeredWindow requires.
    void Blend(
        const int x,
        const int y,
        const COLORREF color,
        const double alpha) noexcept
    {
        if (!Contains(x, y))
        {
            return;
        }

        const double sourceAlpha = Clamp01(alpha);
        if (sourceAlpha <= 0.0)
        {
            return;
        }

        BYTE* const pixel = bits +
            (static_cast<size_t>(y) * static_cast<size_t>(width) +
                static_cast<size_t>(x)) * 4;
        const double inverse = 1.0 - sourceAlpha;
        const auto channel = [&](const int index, const int sourceValue)
        {
            const double source =
                static_cast<double>(sourceValue) / 255.0 * sourceAlpha;
            const double destination = static_cast<double>(pixel[index]) / 255.0;
            const double result = source + destination * inverse;
            pixel[index] = static_cast<BYTE>(std::lround(Clamp01(result) * 255.0));
        };

        channel(0, GetBValue(color));
        channel(1, GetGValue(color));
        channel(2, GetRValue(color));

        const double destinationAlpha = static_cast<double>(pixel[3]) / 255.0;
        const double resultAlpha = sourceAlpha + destinationAlpha * inverse;
        pixel[3] = static_cast<BYTE>(std::lround(Clamp01(resultAlpha) * 255.0));
    }
};

struct DibSurface
{
    HDC deviceContext{};
    HBITMAP bitmap{};
    HGDIOBJ previousBitmap{};
    BYTE* bits{};

    DibSurface() = default;
    DibSurface(const DibSurface&) = delete;
    DibSurface& operator=(const DibSurface&) = delete;

    ~DibSurface()
    {
        Release();
    }

    bool Create(const int width, const int height)
    {
        deviceContext = CreateCompatibleDC(nullptr);
        if (deviceContext == nullptr)
        {
            return false;
        }

        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(info.bmiHeader);
        info.bmiHeader.biWidth = width;
        // A negative height gives a top-down buffer, so row zero is the top row.
        info.bmiHeader.biHeight = -height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        void* pixels = nullptr;
        bitmap = CreateDIBSection(
            deviceContext,
            &info,
            DIB_RGB_COLORS,
            &pixels,
            nullptr,
            0);
        if (bitmap == nullptr || pixels == nullptr)
        {
            Release();
            return false;
        }

        bits = static_cast<BYTE*>(pixels);
        previousBitmap = SelectObject(deviceContext, bitmap);
        return true;
    }

    void Release()
    {
        if (deviceContext != nullptr)
        {
            if (previousBitmap != nullptr)
            {
                SelectObject(deviceContext, previousBitmap);
                previousBitmap = nullptr;
            }

            DeleteDC(deviceContext);
            deviceContext = nullptr;
        }

        if (bitmap != nullptr)
        {
            DeleteObject(bitmap);
            bitmap = nullptr;
        }

        bits = nullptr;
    }
};

// Which of the entry's two type sizes a font is being made for. Segoe UI Variable is
// optically sized - the same design drawn differently per size band - so the two sizes want
// different cuts rather than the same cut scaled.
enum class EntryTypeRole : unsigned char
{
    Capsule,
    Segment,
};

// The semibold face by name, not by weight. Asking a family for FW_SEMIBOLD resolves to
// tmWeight 700 - GDI's mapper rounds up to the Bold face rather than picking the Semibold one
// - and full bold at this size fills its own counters and reads as ragged. Each of these is a
// face in its own right and resolves to 600 exactly.
//
// Segoe UI Variable is the Windows 11 UI face. Its Text cut is drawn for body sizes and its
// Small cut for captions: both carry a larger x-height and looser spacing than Segoe UI at the
// same em, which is most of what makes the capsule read cleanly against the taskbar. It
// shipped with Windows 11; on Windows 10 it is absent, and GDI's mapper would substitute
// something arbitrary rather than the obvious neighbour, so availability is resolved once and
// the whole entry falls back to Segoe UI Semibold together. Mixing the two across the two
// capsules would be worse than using the older face for both.
constexpr wchar_t kEntryCapsuleFontFace[] = L"Segoe UI Variable Text Semibold";
constexpr wchar_t kEntrySegmentFontFace[] = L"Segoe UI Variable Small Semibol";
constexpr wchar_t kEntryFallbackFontFace[] = L"Segoe UI Semibold";

int CALLBACK RecordFontFamilyFound(
    const LOGFONTW*,
    const TEXTMETRICW*,
    const DWORD,
    const LPARAM parameter)
{
    *reinterpret_cast<bool*>(parameter) = true;
    return 0;
}

bool IsFontFamilyInstalled(const wchar_t* const face)
{
    const HDC screen = GetDC(nullptr);
    if (screen == nullptr)
    {
        return false;
    }

    LOGFONTW request{};
    request.lfCharSet = DEFAULT_CHARSET;
    wcscpy_s(request.lfFaceName, face);
    bool found = false;
    EnumFontFamiliesExW(
        screen,
        &request,
        RecordFontFamilyFound,
        reinterpret_cast<LPARAM>(&found),
        0);
    ReleaseDC(nullptr, screen);
    return found;
}

const wchar_t* ResolveEntryFontFace(const EntryTypeRole role)
{
    // Fonts are not installed and uninstalled underneath a running entry, and the entry
    // creates a font per render pass; resolving this once keeps a family enumeration off
    // every repaint.
    static const bool variableAvailable =
        IsFontFamilyInstalled(kEntryCapsuleFontFace) &&
        IsFontFamilyInstalled(kEntrySegmentFontFace);
    if (!variableAvailable)
    {
        return kEntryFallbackFontFace;
    }

    return role == EntryTypeRole::Segment
        ? kEntrySegmentFontFace
        : kEntryCapsuleFontFace;
}

HFONT CreateEntryFont(const int pixelHeight, const EntryTypeRole role)
{
    return CreateFontW(
        -pixelHeight,
        0,
        0,
        0,
        FW_SEMIBOLD,
        FALSE,
        FALSE,
        FALSE,
        DEFAULT_CHARSET,
        OUT_DEFAULT_PRECIS,
        CLIP_DEFAULT_PRECIS,
        // ClearType, but read as three horizontal samples rather than as colour - see
        // CoverageFromMask. Where the system has subpixel rendering turned off this degrades
        // to plain grey-scale antialiasing and the same read still produces the right value.
        CLEARTYPE_QUALITY,
        DEFAULT_PITCH | FF_SWISS,
        ResolveEntryFontFace(role));
}

// One text-mask pixel as coverage. ClearType writes the three subpixel samples into B, G and
// R; averaging them recovers a grey-scale coverage sampled at three times the horizontal
// resolution, which is what smooths the diagonals and curves that a per-pixel mask leaves
// notched. Taking the maximum instead - the obvious reading - keeps the fringe and throws the
// extra resolution away.
BYTE CoverageFromMask(const BYTE* const pixel) noexcept
{
    const int total = static_cast<int>(pixel[0]) +
        static_cast<int>(pixel[1]) +
        static_cast<int>(pixel[2]);
    return static_cast<BYTE>((total + 1) / 3);
}

// Holds one screen DC and one font for the whole render pass. A segmented entry measures
// every segment, and creating a DC and a font per measurement would mean a dozen GDI object
// round trips per animation frame.
struct TextMeasurer
{
    HDC screen{};
    HFONT font{};
    HGDIOBJ previousFont{};

    TextMeasurer(const int fontPixelHeight, const EntryTypeRole role)
    {
        screen = GetDC(nullptr);
        if (screen == nullptr)
        {
            return;
        }

        font = CreateEntryFont(fontPixelHeight, role);
        if (font != nullptr)
        {
            previousFont = SelectObject(screen, font);
        }
    }

    TextMeasurer(const TextMeasurer&) = delete;
    TextMeasurer& operator=(const TextMeasurer&) = delete;

    ~TextMeasurer()
    {
        if (screen == nullptr)
        {
            return;
        }

        if (previousFont != nullptr)
        {
            SelectObject(screen, previousFont);
        }

        if (font != nullptr)
        {
            DeleteObject(font);
        }

        ReleaseDC(nullptr, screen);
    }

    [[nodiscard]] bool IsReady() const noexcept
    {
        return screen != nullptr && font != nullptr;
    }

    [[nodiscard]] int Measure(const std::wstring& text) const
    {
        SIZE extent{};
        if (!IsReady() ||
            text.empty() ||
            GetTextExtentPoint32W(
                screen,
                text.c_str(),
                static_cast<int>(text.size()),
                &extent) == FALSE)
        {
            return 0;
        }

        return extent.cx;
    }
};

int ResolveFontPixelHeight(const EntryRenderRequest& request)
{
    const int base = ScaleLogical(kEntryFontSizeLogical, request.dpi);
    if (!request.compact)
    {
        return base;
    }

    const int capsuleHeight = static_cast<int>(RectHeightOf(request.capsule));
    return std::max(base, capsuleHeight / 2);
}

// True when a condition carries an illustration. Only the weather glyphs do: Unknown and
// None state no reading, and a hardware glyph has no sky to paint. Listing them rather than
// excluding two is what keeps a newly added glyph from silently inheriting an illustration.
bool HasBackdrop(const EntryIcon icon)
{
    switch (icon)
    {
    case EntryIcon::ClearDay:
    case EntryIcon::ClearNight:
    case EntryIcon::PartlyCloudyDay:
    case EntryIcon::PartlyCloudyNight:
    case EntryIcon::Cloudy:
    case EntryIcon::Fog:
    case EntryIcon::Drizzle:
    case EntryIcon::Rain:
    case EntryIcon::Snow:
    case EntryIcon::Thunderstorm:
        return true;
    default:
        return false;
    }
}

// Everything right of the text: the trailing padding, plus the illustration's own room when
// there is one to draw.
int TrailingWidthLogical(const EntryIcon icon)
{
    return kEntryTrailingPaddingLogical +
        (HasBackdrop(icon) ? kEntryMotifWidthLogical : 0);
}

// Everything left of the text, in logical pixels: padding, indicator, its gap, and the
// condition glyph with its own gap when one is present.
int LeadingWidthLogical(const EntryIcon icon)
{
    int width = kEntryLeadingPaddingLogical +
        kEntryIndicatorWidthLogical +
        kEntryIndicatorGapLogical;
    if (icon != EntryIcon::None)
    {
        width += kEntryIconSizeLogical + kEntryIconGapLogical;
    }

    return width;
}

RECT ResolveIconRect(const EntryRenderRequest& request)
{
    const int size = ScaleLogical(kEntryIconSizeLogical, request.dpi);
    const int left = request.capsule.left +
        ScaleLogical(
            kEntryLeadingPaddingLogical +
                kEntryIndicatorWidthLogical +
                kEntryIndicatorGapLogical,
            request.dpi);
    const int centerY = (request.capsule.top + request.capsule.bottom) / 2;
    const int top = centerY - size / 2;
    return RECT{left, top, left + size, top + size};
}

struct SegmentLayout
{
    RECT iconRect{};
    RECT textRect{};
    // The band the reading's history is washed across. Spans the whole cell, text included:
    // the number sits on its own graph the way a network monitor's does.
    RECT graphRect{};
    int dividerCenterX{};
    bool hasDivider{};
};

// Lays the segments out in columns of two after the indicator. Every cell in a column starts
// at the same x so the readings line up vertically; every column owns the same fixed-width
// slot, so a changing number cannot move any column after it. Neighbouring columns are
// separated by a gap, a hairline spanning both rows, and another gap.
// One column's logical width for the pair of readings it holds: content-measured, plus
// slack, quantized up, floored at the old fixed slot and capped. Layout and measurement
// both derive from this, which is what keeps the reserved capsule width and the painted
// strip in exact agreement - a one-pixel disagreement here drops a whole column.
std::vector<int> ResolveSegmentColumnWidthsLogical(
    const std::vector<EntrySegment>& segments,
    const UINT dpi)
{
    const size_t count = segments.size();
    std::vector<int> widths;
    widths.reserve((count + kEntrySegmentRowsPerColumn - 1) / kEntrySegmentRowsPerColumn);

    const HDC screen = GetDC(nullptr);
    HFONT font = screen == nullptr
        ? nullptr
        : CreateEntryFont(
              ScaleLogical(kEntrySegmentFontSizeLogical, dpi),
              EntryTypeRole::Segment);
    const HGDIOBJ previousFont =
        font == nullptr ? nullptr : SelectObject(screen, font);

    const int iconSize = ScaleLogical(kEntrySegmentIconSizeLogical, dpi);
    const int iconGap = ScaleLogical(kEntrySegmentIconGapLogical, dpi);
    for (size_t first = 0; first < count; first += kEntrySegmentRowsPerColumn)
    {
        const size_t past = std::min(first + kEntrySegmentRowsPerColumn, count);
        int contentDevice = 0;
        for (size_t index = first; index < past; ++index)
        {
            const EntrySegment& segment = segments[index];
            int rowDevice = segment.icon == EntryIcon::None ? 0 : iconSize + iconGap;
            SIZE extent{};
            if (font != nullptr &&
                !segment.text.empty() &&
                GetTextExtentPoint32W(
                    screen,
                    segment.text.c_str(),
                    static_cast<int>(segment.text.size()),
                    &extent) != FALSE)
            {
                rowDevice += extent.cx;
            }

            contentDevice = std::max(contentDevice, rowDevice);
        }

        // Measurement failure (no DC, no font) leaves content at the icon alone; the floor
        // then hands back the old fixed slot, so a degraded machine degrades to the old look.
        int logical = ToLogical(contentDevice, dpi) + kEntrySegmentColumnSlackLogical;
        logical = ((logical + kEntrySegmentColumnQuantumLogical - 1) /
                      kEntrySegmentColumnQuantumLogical) *
            kEntrySegmentColumnQuantumLogical;
        widths.push_back(std::clamp(
            logical,
            kEntrySegmentColumnWidthLogical,
            kEntrySegmentColumnMaxWidthLogical));
    }

    if (font != nullptr)
    {
        SelectObject(screen, previousFont);
        DeleteObject(font);
    }

    if (screen != nullptr)
    {
        ReleaseDC(nullptr, screen);
    }

    return widths;
}

std::vector<SegmentLayout> ResolveSegmentLayout(const EntryRenderRequest& request)
{
    std::vector<SegmentLayout> layouts;
    layouts.reserve(request.segments.size());

    const int iconSize = ScaleLogical(kEntrySegmentIconSizeLogical, request.dpi);
    const int iconGap = ScaleLogical(kEntrySegmentIconGapLogical, request.dpi);
    const std::vector<int> columnWidthsLogical =
        ResolveSegmentColumnWidthsLogical(request.segments, request.dpi);
    const int columnGap = ScaleLogical(kEntrySegmentColumnGapLogical, request.dpi);
    const int dividerWidth = ScaleLogical(kEntryDividerWidthLogical, request.dpi);
    const int rowHeight = ScaleLogical(kEntrySegmentRowHeightLogical, request.dpi);
    const int graphInset = ScaleLogical(kEntrySparklineInsetLogical, request.dpi);
    // The readings live in their own capsule, which carries no indicator: that one belongs to
    // the main capsule, where it reports the panel's state.
    const RECT capsule = request.monitorCapsule;
    const int centerY = (capsule.top + capsule.bottom) / 2;

    int cursor = capsule.left +
        ScaleLogical(kEntryLeadingPaddingLogical, request.dpi);

    // A column that will not fit whole is dropped rather than clipped. A strip that ends in
    // half a glyph reads as a rendering fault; one that simply ends does not, and the card
    // still shows every configured reading. Columns are dropped together so the surviving
    // strip never shows a top reading with an empty slot under it.
    const int limit = capsule.right -
        ScaleLogical(kEntryTrailingPaddingLogical, request.dpi);

    const size_t count = request.segments.size();
    for (size_t first = 0; first < count; first += kEntrySegmentRowsPerColumn)
    {
        const size_t past = std::min(first + kEntrySegmentRowsPerColumn, count);
        const int rowCount = static_cast<int>(past - first);
        const int columnWidth = ScaleLogical(
            columnWidthsLogical[first / kEntrySegmentRowsPerColumn],
            request.dpi);

        if (!layouts.empty() && cursor + columnWidth > limit)
        {
            break;
        }

        // A column of one - the odd reading at the end, or a strip with a single segment -
        // sits on the centre line rather than pretending its missing partner is below it.
        const int columnTop = centerY - rowHeight * rowCount / 2;
        for (size_t index = first; index < past; ++index)
        {
            const EntrySegment& segment = request.segments[index];
            const int rowTop =
                columnTop + rowHeight * static_cast<int>(index - first);
            const int rowCenterY = rowTop + rowHeight / 2;

            SegmentLayout layout{};
            int cellCursor = cursor;
            if (segment.icon != EntryIcon::None)
            {
                layout.iconRect = RECT{
                    cellCursor,
                    rowCenterY - iconSize / 2,
                    cellCursor + iconSize,
                    rowCenterY + iconSize / 2};
                cellCursor += iconSize + iconGap;
            }

            layout.textRect = RECT{
                cellCursor,
                rowTop,
                cursor + columnWidth,
                rowTop + rowHeight};
            layout.graphRect = RECT{
                cursor,
                rowTop + graphInset,
                cursor + columnWidth,
                rowTop + rowHeight - graphInset};
            layouts.push_back(layout);
        }

        cursor += columnWidth;
        if (past < count)
        {
            layouts.back().hasDivider = true;
            layouts.back().dividerCenterX = cursor + columnGap;
            cursor += columnGap * 2 + dividerWidth;
        }
    }

    // The last column that survived the fit check has nothing to its right to separate it
    // from, so its divider goes with the columns that were dropped.
    if (!layouts.empty())
    {
        layouts.back().hasDivider = false;
    }

    return layouts;
}

RECT ResolveTextRect(const EntryRenderRequest& request)
{
    if (request.compact)
    {
        return request.capsule;
    }

    RECT textRect = request.capsule;
    textRect.left += ScaleLogical(LeadingWidthLogical(request.icon), request.dpi);
    textRect.right -= ScaleLogical(TrailingWidthLogical(request.icon), request.dpi);
    return textRect;
}

// Draws the text with GDI into a scratch surface and reads the result back as a coverage
// mask. GDI leaves the alpha byte untouched, so text cannot be drawn straight into a
// premultiplied buffer; white on black gives exactly the coverage we need instead.
bool ComposeText(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const COLORREF color,
    const double alpha)
{
    if (request.text.empty() || alpha <= 0.0)
    {
        return true;
    }

    DibSurface mask;
    if (!mask.Create(canvas.width, canvas.height))
    {
        return false;
    }

    HFONT font = CreateEntryFont(
        ResolveFontPixelHeight(request),
        EntryTypeRole::Capsule);
    if (font == nullptr)
    {
        return false;
    }

    HGDIOBJ previousFont = SelectObject(mask.deviceContext, font);
    SetBkMode(mask.deviceContext, TRANSPARENT);
    SetTextColor(mask.deviceContext, RGB(255, 255, 255));

    RECT textRect = ResolveTextRect(request);
    const UINT format = DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX |
        (request.compact ? DT_CENTER : (DT_LEFT | DT_END_ELLIPSIS));
    DrawTextW(
        mask.deviceContext,
        request.text.c_str(),
        static_cast<int>(request.text.size()),
        &textRect,
        format);

    SelectObject(mask.deviceContext, previousFont);
    DeleteObject(font);

    for (int y = 0; y < canvas.height; ++y)
    {
        for (int x = 0; x < canvas.width; ++x)
        {
            const BYTE* const source = mask.bits +
                (static_cast<size_t>(y) * static_cast<size_t>(canvas.width) +
                    static_cast<size_t>(x)) * 4;
            const BYTE coverage = CoverageFromMask(source);
            if (coverage == 0)
            {
                continue;
            }

            canvas.Blend(
                x,
                y,
                color,
                alpha * static_cast<double>(coverage) / 255.0);
        }
    }

    return true;
}

// The capsule as it is actually drawn, press scale included. The fill and the condition
// illustration have to agree on it exactly: the illustration is clipped by this shape, and a
// half-pixel disagreement would show up as a coloured fringe along the edge.
struct CapsuleShape
{
    double centerX{};
    double centerY{};
    double halfWidth{};
    double halfHeight{};
    double radius{};

    [[nodiscard]] double CoverageAt(const double pixelX, const double pixelY) const
    {
        return CoverageFromDistance(RoundedRectDistance(
            pixelX,
            pixelY,
            centerX,
            centerY,
            halfWidth,
            halfHeight,
            radius));
    }
};

CapsuleShape ResolveCapsuleShape(
    const RECT& capsule,
    const EntryVisualState& state)
{
    const double scale = 1.0 - (1.0 - kPressScale) * Clamp01(state.pressed);
    CapsuleShape shape{};
    shape.centerX =
        (static_cast<double>(capsule.left) +
            static_cast<double>(capsule.right)) / 2.0;
    shape.centerY =
        (static_cast<double>(capsule.top) +
            static_cast<double>(capsule.bottom)) / 2.0;
    shape.halfWidth = RectWidthOf(capsule) / 2.0 * scale;
    shape.halfHeight = RectHeightOf(capsule) / 2.0 * scale;
    shape.radius = std::min(shape.halfWidth, shape.halfHeight);
    return shape;
}

void ComposeCapsule(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const RECT& capsule,
    const EntryTheme& theme,
    const EntryVisualState& state)
{
    const CapsuleShape shape = ResolveCapsuleShape(capsule, state);
    const double centerY = shape.centerY;
    const double halfHeight = shape.halfHeight;

    double fillAlpha = theme.restAlpha +
        (theme.hoverAlpha - theme.restAlpha) * Clamp01(state.hover) +
        (theme.pressedAlpha - theme.restAlpha) * Clamp01(state.pressed) +
        (theme.activeAlpha - theme.restAlpha) * Clamp01(state.active);
    fillAlpha = Clamp01(fillAlpha);

    const COLORREF fillColor = theme.highContrast
        ? MixColor(
            theme.highContrastFace,
            theme.highContrastActiveFace,
            Clamp01(state.active))
        : MixColor(theme.surfaceTint, theme.accent, Clamp01(state.active) * 0.85);
    if (theme.highContrast)
    {
        fillAlpha = 1.0;
    }

    const double borderWidth = static_cast<double>(ScaleLogical(1, request.dpi));
    const double rimReference = std::max(theme.restAlpha, 0.01);

    for (int y = 0; y < canvas.height; ++y)
    {
        const double pixelY = static_cast<double>(y) + 0.5;
        // A gentle top-to-bottom falloff gives the fill some depth without becoming a
        // decorative gradient of its own.
        const double verticalPosition = halfHeight <= 0.0
            ? 0.0
            : Clamp01((pixelY - (centerY - halfHeight)) / (halfHeight * 2.0));
        const double gradient = theme.highContrast
            ? 1.0
            : 1.0 + kFillGradientAmount * (0.5 - verticalPosition);

        for (int x = 0; x < canvas.width; ++x)
        {
            const double pixelX = static_cast<double>(x) + 0.5;
            const double distance = RoundedRectDistance(
                pixelX,
                pixelY,
                shape.centerX,
                centerY,
                shape.halfWidth,
                halfHeight,
                shape.radius);
            const double coverage = CoverageFromDistance(distance);
            if (coverage <= 0.0)
            {
                continue;
            }

            canvas.Blend(x, y, fillColor, coverage * fillAlpha * gradient);

            if (theme.highContrast)
            {
                // High contrast restores the system's one-DIP boundary rather than relying
                // on the translucent edge the theme has just taken away.
                const double innerCoverage =
                    CoverageFromDistance(distance + borderWidth);
                canvas.Blend(
                    x,
                    y,
                    theme.highContrastBorder,
                    coverage - innerCoverage);
                continue;
            }

            if (!theme.darkStrip || verticalPosition > 0.5)
            {
                continue;
            }

            // The rim exists only on the top half and fades out by the midpoint.
            const double rimCoverage =
                coverage - CoverageFromDistance(distance + borderWidth);
            if (rimCoverage <= 0.0)
            {
                continue;
            }

            const double falloff = 1.0 - verticalPosition * 2.0;
            canvas.Blend(
                x,
                y,
                RGB(255, 255, 255),
                rimCoverage * kTopHighlightAlpha * falloff * fillAlpha / rimReference);
        }
    }
}

// --- condition glyphs -------------------------------------------------------------------
//
// Each glyph is described in a unit box and rasterised into a coverage mask, then blended
// once. Accumulating coverage with max() rather than compositing shape by shape is what
// keeps overlapping parts of a glyph - the three lobes of a cloud, say - from darkening
// each other into visible seams at partial alpha.

struct IconMask
{
    std::vector<double> coverage;
    int width{};
    int height{};
    double unit{};
    double originX{};
    double originY{};

    void Union(const int x, const int y, const double value)
    {
        if (x < 0 || y < 0 || x >= width || y >= height || value <= 0.0)
        {
            return;
        }

        double& target = coverage[
            static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(x)];
        target = std::max(target, Clamp01(value));
    }

    void Subtract(const int x, const int y, const double value)
    {
        if (x < 0 || y < 0 || x >= width || y >= height || value <= 0.0)
        {
            return;
        }

        double& target = coverage[
            static_cast<size_t>(y) * static_cast<size_t>(width) + static_cast<size_t>(x)];
        target = Clamp01(target - Clamp01(value));
    }
};

// Adds a rounded rectangle given in unit-box coordinates. A circle is the special case where
// width equals height and the radius is half of it.
void AddUnitRoundedRect(
    IconMask& mask,
    const double centerX,
    const double centerY,
    const double width,
    const double height,
    const double radius,
    const bool subtract = false)
{
    const double halfWidth = width * mask.unit / 2.0;
    const double halfHeight = height * mask.unit / 2.0;
    const double pixelCenterX = mask.originX + centerX * mask.unit;
    const double pixelCenterY = mask.originY + centerY * mask.unit;
    const double pixelRadius = radius * mask.unit;

    const int left = static_cast<int>(std::floor(pixelCenterX - halfWidth - 2.0));
    const int right = static_cast<int>(std::ceil(pixelCenterX + halfWidth + 2.0));
    const int top = static_cast<int>(std::floor(pixelCenterY - halfHeight - 2.0));
    const int bottom = static_cast<int>(std::ceil(pixelCenterY + halfHeight + 2.0));

    for (int y = top; y <= bottom; ++y)
    {
        for (int x = left; x <= right; ++x)
        {
            const double coverage = CoverageFromDistance(RoundedRectDistance(
                static_cast<double>(x) + 0.5,
                static_cast<double>(y) + 0.5,
                pixelCenterX,
                pixelCenterY,
                halfWidth,
                halfHeight,
                pixelRadius));
            if (subtract)
            {
                mask.Subtract(x, y, coverage);
            }
            else
            {
                mask.Union(x, y, coverage);
            }
        }
    }
}

void AddUnitCircle(
    IconMask& mask,
    const double centerX,
    const double centerY,
    const double radius,
    const bool subtract = false)
{
    AddUnitRoundedRect(
        mask,
        centerX,
        centerY,
        radius * 2.0,
        radius * 2.0,
        radius,
        subtract);
}

// The bolt is the one shape a rounded rectangle cannot express. Supersampled
// point-in-polygon is plenty at this size and avoids adding a second rasteriser.
void AddUnitPolygon(
    IconMask& mask,
    const double* const pointsX,
    const double* const pointsY,
    const int pointCount)
{
    constexpr int kSamples = 4;
    double minX = pointsX[0];
    double maxX = pointsX[0];
    double minY = pointsY[0];
    double maxY = pointsY[0];
    for (int index = 1; index < pointCount; ++index)
    {
        minX = std::min(minX, pointsX[index]);
        maxX = std::max(maxX, pointsX[index]);
        minY = std::min(minY, pointsY[index]);
        maxY = std::max(maxY, pointsY[index]);
    }

    const int left = static_cast<int>(std::floor(mask.originX + minX * mask.unit)) - 1;
    const int right = static_cast<int>(std::ceil(mask.originX + maxX * mask.unit)) + 1;
    const int top = static_cast<int>(std::floor(mask.originY + minY * mask.unit)) - 1;
    const int bottom = static_cast<int>(std::ceil(mask.originY + maxY * mask.unit)) + 1;

    for (int y = top; y <= bottom; ++y)
    {
        for (int x = left; x <= right; ++x)
        {
            int hits = 0;
            for (int sampleY = 0; sampleY < kSamples; ++sampleY)
            {
                for (int sampleX = 0; sampleX < kSamples; ++sampleX)
                {
                    const double sampleUnitX =
                        ((static_cast<double>(x) +
                            (static_cast<double>(sampleX) + 0.5) / kSamples) -
                            mask.originX) / mask.unit;
                    const double sampleUnitY =
                        ((static_cast<double>(y) +
                            (static_cast<double>(sampleY) + 0.5) / kSamples) -
                            mask.originY) / mask.unit;

                    bool inside = false;
                    for (int i = 0, j = pointCount - 1; i < pointCount; j = i++)
                    {
                        if ((pointsY[i] > sampleUnitY) != (pointsY[j] > sampleUnitY) &&
                            sampleUnitX < (pointsX[j] - pointsX[i]) *
                                (sampleUnitY - pointsY[i]) /
                                (pointsY[j] - pointsY[i]) + pointsX[i])
                        {
                            inside = !inside;
                        }
                    }

                    if (inside)
                    {
                        ++hits;
                    }
                }
            }

            if (hits > 0)
            {
                mask.Union(
                    x,
                    y,
                    static_cast<double>(hits) /
                        static_cast<double>(kSamples * kSamples));
            }
        }
    }
}

// The cloud in its own terms: three lobes over a flat base, spanning 0.69 x 0.49 of the unit
// box around (0.515, 0.485). `scale` and the centre place that silhouette anywhere, which is
// what lets the 18 DIP glyph and the much larger illustration share one drawing.
void AddCloudAt(
    IconMask& mask,
    const double centerX,
    const double centerY,
    const double scale)
{
    constexpr double kAnchorX = 0.515;
    constexpr double kAnchorY = 0.485;
    const auto placeX = [&](const double x) { return centerX + (x - kAnchorX) * scale; };
    const auto placeY = [&](const double y) { return centerY + (y - kAnchorY) * scale; };

    AddUnitCircle(mask, placeX(0.34), placeY(0.54), 0.17 * scale);
    AddUnitCircle(mask, placeX(0.52), placeY(0.46), 0.22 * scale);
    AddUnitCircle(mask, placeX(0.71), placeY(0.56), 0.15 * scale);
    AddUnitRoundedRect(
        mask,
        placeX(0.52),
        placeY(0.63),
        0.58 * scale,
        0.20 * scale,
        0.10 * scale);
}

void AddCloud(IconMask& mask, const double offsetY)
{
    AddCloudAt(mask, 0.515, 0.485 + offsetY, 1.0);
}

void AddSun(
    IconMask& mask,
    const double centerX,
    const double centerY,
    const double radius)
{
    AddUnitCircle(mask, centerX, centerY, radius);
    constexpr int kRayCount = 8;
    const double rayDistance = radius + 0.115;
    for (int index = 0; index < kRayCount; ++index)
    {
        const double angle = 6.283185307179586 *
            static_cast<double>(index) / static_cast<double>(kRayCount);
        AddUnitCircle(
            mask,
            centerX + std::cos(angle) * rayDistance,
            centerY + std::sin(angle) * rayDistance,
            0.052);
    }
}

void AddCrescent(
    IconMask& mask,
    const double centerX,
    const double centerY,
    const double radius)
{
    AddUnitCircle(mask, centerX, centerY, radius);
    AddUnitCircle(
        mask,
        centerX + radius * 0.62,
        centerY - radius * 0.46,
        radius * 0.92,
        true);
}

void AddFall(IconMask& mask, const int count, const bool asDots, const double length)
{
    constexpr double kStart = 0.32;
    constexpr double kStep = 0.20;
    for (int index = 0; index < count; ++index)
    {
        const double x = kStart + kStep * static_cast<double>(index);
        if (asDots)
        {
            AddUnitCircle(mask, x, 0.86, 0.055);
        }
        else
        {
            AddUnitRoundedRect(mask, x, 0.86, 0.075, length, 0.038);
        }
    }
}

// A chip: a rounded body with a hollow core and pins along two sides. At 18 DIP the pins are
// what separate it from every other rounded square in the set.
void AddChip(IconMask& mask)
{
    AddUnitRoundedRect(mask, 0.5, 0.5, 0.56, 0.56, 0.12);
    AddUnitRoundedRect(mask, 0.5, 0.5, 0.28, 0.28, 0.06, true);
    for (int index = 0; index < 3; ++index)
    {
        const double offset = 0.34 + 0.16 * static_cast<double>(index);
        AddUnitRoundedRect(mask, offset, 0.15, 0.055, 0.12, 0.027);
        AddUnitRoundedRect(mask, offset, 0.85, 0.055, 0.12, 0.027);
    }
}

// A memory module: a wide body notched from below, the shape of the contact edge.
void AddMemoryStick(IconMask& mask)
{
    AddUnitRoundedRect(mask, 0.5, 0.46, 0.68, 0.40, 0.08);
    for (int index = 0; index < 3; ++index)
    {
        const double offset = 0.32 + 0.18 * static_cast<double>(index);
        AddUnitRoundedRect(mask, offset, 0.66, 0.07, 0.16, 0.03, true);
    }

    AddUnitRoundedRect(mask, 0.5, 0.79, 0.50, 0.09, 0.04);
}

// A graphics board: a wider body than the chip, with a fan hub inside it.
void AddGraphicsBoard(IconMask& mask)
{
    AddUnitRoundedRect(mask, 0.5, 0.5, 0.76, 0.48, 0.10);
    AddUnitCircle(mask, 0.5, 0.5, 0.15, true);
    AddUnitCircle(mask, 0.5, 0.5, 0.06);
}

// A disk stack: three platters seen edge on.
void AddDiskStack(IconMask& mask)
{
    for (int index = 0; index < 3; ++index)
    {
        AddUnitRoundedRect(
            mask,
            0.5,
            0.28 + 0.22 * static_cast<double>(index),
            0.66,
            0.13,
            0.065);
    }
}

// A direction arrow. `up` flips it; the two network readings differ only in this, which is
// exactly how the taskbar convention reads them.
void AddTransferArrow(IconMask& mask, const bool up)
{
    const double tip = up ? 0.16 : 0.84;
    const double base = up ? 0.52 : 0.48;
    const double headX[] = {0.5, 0.16, 0.84};
    const double headY[] = {tip, base, base};
    AddUnitPolygon(mask, headX, headY, 3);
    AddUnitRoundedRect(mask, 0.5, up ? 0.70 : 0.30, 0.22, 0.34, 0.08);
}

// A fan: a ring with a hub. Distinct from the unknown ring by the hub and a thinner rim.
void AddFanGlyph(IconMask& mask)
{
    AddUnitCircle(mask, 0.5, 0.5, 0.34);
    AddUnitCircle(mask, 0.5, 0.5, 0.25, true);
    AddUnitCircle(mask, 0.5, 0.5, 0.10);
}

void BuildIconMask(IconMask& mask, const EntryIcon icon)
{
    switch (icon)
    {
    case EntryIcon::ClearDay:
        AddSun(mask, 0.5, 0.5, 0.23);
        break;
    case EntryIcon::ClearNight:
        AddCrescent(mask, 0.47, 0.5, 0.30);
        break;
    case EntryIcon::PartlyCloudyDay:
        AddSun(mask, 0.31, 0.28, 0.13);
        AddCloud(mask, 0.06);
        break;
    case EntryIcon::PartlyCloudyNight:
        AddCrescent(mask, 0.27, 0.23, 0.19);
        AddCloud(mask, 0.09);
        break;
    case EntryIcon::Cloudy:
        AddCloud(mask, 0.0);
        break;
    case EntryIcon::Fog:
        AddCloud(mask, -0.10);
        AddUnitRoundedRect(mask, 0.46, 0.80, 0.56, 0.075, 0.037);
        AddUnitRoundedRect(mask, 0.54, 0.94, 0.44, 0.075, 0.037);
        break;
    case EntryIcon::Drizzle:
        AddCloud(mask, -0.10);
        AddFall(mask, 2, false, 0.14);
        break;
    case EntryIcon::Rain:
        AddCloud(mask, -0.10);
        AddFall(mask, 3, false, 0.22);
        break;
    case EntryIcon::Snow:
        AddCloud(mask, -0.10);
        AddFall(mask, 3, true, 0.0);
        break;
    case EntryIcon::Thunderstorm:
    {
        AddCloud(mask, -0.12);
        const double boltX[] = {0.62, 0.38, 0.51, 0.34, 0.66, 0.51};
        const double boltY[] = {0.62, 0.90, 0.90, 1.10, 0.83, 0.83};
        AddUnitPolygon(mask, boltX, boltY, 6);
        break;
    }
    case EntryIcon::Cpu:
        AddChip(mask);
        break;
    case EntryIcon::Memory:
        AddMemoryStick(mask);
        break;
    case EntryIcon::Gpu:
        AddGraphicsBoard(mask);
        break;
    case EntryIcon::Disk:
        AddDiskStack(mask);
        break;
    case EntryIcon::NetworkUp:
        AddTransferArrow(mask, true);
        break;
    case EntryIcon::NetworkDown:
        AddTransferArrow(mask, false);
        break;
    case EntryIcon::Fan:
        AddFanGlyph(mask);
        break;
    case EntryIcon::Unknown:
        // A neutral ring: it reads as "no reading" without imitating a real condition.
        AddUnitCircle(mask, 0.5, 0.5, 0.30);
        AddUnitCircle(mask, 0.5, 0.5, 0.19, true);
        break;
    case EntryIcon::None:
    default:
        break;
    }
}

// Rasterises one glyph into `box`. The mask covers only the box, clipped to the canvas, so a
// strip of several glyphs costs a few small buffers instead of one canvas-sized buffer each.
void ComposeGlyph(
    Canvas& canvas,
    const RECT& box,
    const EntryIcon icon,
    const COLORREF color,
    const double alpha)
{
    const int size = box.right - box.left;
    if (icon == EntryIcon::None || alpha <= 0.0 || size <= 0)
    {
        return;
    }

    // A glyph can reach slightly outside its box - the thunderbolt drops below it - so the
    // mask is padded before being clipped to the canvas.
    const int pad = std::max(2, size / 4);
    const int left = std::max(0, static_cast<int>(box.left) - pad);
    const int top = std::max(0, static_cast<int>(box.top) - pad);
    const int right = std::min(canvas.width, static_cast<int>(box.right) + pad);
    const int bottom = std::min(canvas.height, static_cast<int>(box.bottom) + pad);
    if (right <= left || bottom <= top)
    {
        return;
    }

    const int width = right - left;
    const int height = bottom - top;

    IconMask mask{};
    mask.width = width;
    mask.height = height;
    mask.coverage.assign(
        static_cast<size_t>(width) * static_cast<size_t>(height),
        0.0);
    mask.unit = static_cast<double>(size);
    mask.originX = static_cast<double>(box.left - left);
    mask.originY = static_cast<double>(box.top - top);
    BuildIconMask(mask, icon);

    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            const double coverage = mask.coverage[
                static_cast<size_t>(y) * static_cast<size_t>(width) +
                    static_cast<size_t>(x)];
            if (coverage > 0.0)
            {
                canvas.Blend(left + x, top + y, color, coverage * alpha);
            }
        }
    }
}

void ComposeIcon(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const COLORREF color,
    const double alpha)
{
    if (request.compact || request.icon == EntryIcon::None)
    {
        return;
    }

    ComposeGlyph(canvas, ResolveIconRect(request), request.icon, color, alpha);
}

// Washes one reading's recent history across its cell as a filled area, newest at the right.
// The series is already 0..1 - the broker picked the scale, because only it has the raw
// numbers - so this does nothing but map samples to columns and fill downwards.
//
// Sampled per pixel column with linear interpolation rather than drawn as a polygon: the cell
// is a few dozen pixels wide against up to sixty samples, so most columns fall between two of
// them, and interpolating is both simpler and steadier than deciding which sample "wins".
void ComposeSparkline(
    Canvas& canvas,
    const RECT& graphRect,
    const std::vector<double>& history,
    const COLORREF color,
    const double alpha)
{
    const int left = std::max(0, static_cast<int>(graphRect.left));
    const int right = std::min(canvas.width, static_cast<int>(graphRect.right));
    const int top = std::max(0, static_cast<int>(graphRect.top));
    const int bottom = std::min(canvas.height, static_cast<int>(graphRect.bottom));
    if (history.size() < 2 || right <= left || bottom <= top || alpha <= 0.0)
    {
        return;
    }

    const double height = static_cast<double>(bottom - top);
    const double span = static_cast<double>(right - left - 1);
    const double lastIndex = static_cast<double>(history.size() - 1);

    for (int x = left; x < right; ++x)
    {
        const double position = span <= 0.0
            ? lastIndex
            : static_cast<double>(x - left) / span * lastIndex;
        const size_t lower = static_cast<size_t>(position);
        const size_t upper = std::min(lower + 1, history.size() - 1);
        const double blend = position - static_cast<double>(lower);
        const double value = Clamp01(
            Lerp(Clamp01(history[lower]), Clamp01(history[upper]), blend));

        // Zero still leaves the thinnest possible mark, so an idle reading reads as a
        // baseline rather than as a graph that failed to draw.
        const double filled = std::max(value * height, 1.0);
        const double surfaceY = static_cast<double>(bottom) - filled;
        for (int y = std::max(top, static_cast<int>(std::floor(surfaceY)));
            y < bottom;
            ++y)
        {
            // Partial coverage on the topmost row keeps the silhouette smooth without
            // supersampling the whole band.
            const double coverage = Clamp01(
                static_cast<double>(y + 1) - std::max(surfaceY, static_cast<double>(y)));
            if (coverage > 0.0)
            {
                canvas.Blend(x, y, color, alpha * coverage);
            }
        }
    }
}

// The whole segmented strip: each glyph, each already-composed value, and a hairline between
// neighbours. All the text goes through one scratch surface rather than one per segment.
bool ComposeSegments(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const COLORREF color,
    const double alpha)
{
    if (request.segments.empty() || alpha <= 0.0)
    {
        return true;
    }

    // The segmented strip has its own type size - see kEntrySegmentFontSizeLogical - so it
    // does not go through ResolveFontPixelHeight, which serves the single-line modes.
    const int fontPixelHeight =
        ScaleLogical(kEntrySegmentFontSizeLogical, request.dpi);
    const std::vector<SegmentLayout> layouts = ResolveSegmentLayout(request);

    DibSurface textMask;
    if (!textMask.Create(canvas.width, canvas.height))
    {
        return false;
    }

    HFONT font = CreateEntryFont(fontPixelHeight, EntryTypeRole::Segment);
    if (font == nullptr)
    {
        return false;
    }

    // Graphs first: the readings sit on top of their own history, never the other way round.
    for (size_t index = 0; index < layouts.size(); ++index)
    {
        ComposeSparkline(
            canvas,
            layouts[index].graphRect,
            request.segments[index].history,
            color,
            alpha * kEntrySparklineAlpha);
    }

    HGDIOBJ previousFont = SelectObject(textMask.deviceContext, font);
    SetBkMode(textMask.deviceContext, TRANSPARENT);
    SetTextColor(textMask.deviceContext, RGB(255, 255, 255));

    for (size_t index = 0; index < layouts.size(); ++index)
    {
        const EntrySegment& segment = request.segments[index];
        if (segment.text.empty())
        {
            continue;
        }

        RECT textRect = layouts[index].textRect;
        DrawTextW(
            textMask.deviceContext,
            segment.text.c_str(),
            static_cast<int>(segment.text.size()),
            &textRect,
            DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_LEFT | DT_END_ELLIPSIS);
    }

    SelectObject(textMask.deviceContext, previousFont);
    DeleteObject(font);

    for (int y = 0; y < canvas.height; ++y)
    {
        for (int x = 0; x < canvas.width; ++x)
        {
            const BYTE* const source = textMask.bits +
                (static_cast<size_t>(y) * static_cast<size_t>(canvas.width) +
                    static_cast<size_t>(x)) * 4;
            const BYTE coverage = CoverageFromMask(source);
            if (coverage != 0)
            {
                canvas.Blend(
                    x,
                    y,
                    color,
                    alpha * static_cast<double>(coverage) / 255.0);
            }
        }
    }

    const double dividerWidth = static_cast<double>(
        ScaleLogical(kEntryDividerWidthLogical, request.dpi));
    const double dividerHeight = static_cast<double>(
        ScaleLogical(kEntryDividerHeightLogical, request.dpi));
    const double centerY =
        (static_cast<double>(request.monitorCapsule.top) +
            static_cast<double>(request.monitorCapsule.bottom)) / 2.0;

    for (size_t index = 0; index < layouts.size(); ++index)
    {
        const SegmentLayout& layout = layouts[index];
        ComposeGlyph(
            canvas,
            layout.iconRect,
            request.segments[index].icon,
            color,
            alpha);

        if (!layout.hasDivider)
        {
            continue;
        }

        const double centerX = static_cast<double>(layout.dividerCenterX);
        const int left = static_cast<int>(std::floor(centerX - dividerWidth)) - 1;
        const int right = static_cast<int>(std::ceil(centerX + dividerWidth)) + 1;
        const int top = static_cast<int>(std::floor(centerY - dividerHeight / 2.0)) - 1;
        const int bottom = static_cast<int>(std::ceil(centerY + dividerHeight / 2.0)) + 1;

        for (int y = std::max(0, top); y <= std::min(canvas.height - 1, bottom); ++y)
        {
            for (int x = std::max(0, left); x <= std::min(canvas.width - 1, right); ++x)
            {
                const double coverage = CoverageFromDistance(RoundedRectDistance(
                    static_cast<double>(x) + 0.5,
                    static_cast<double>(y) + 0.5,
                    centerX,
                    centerY,
                    dividerWidth / 2.0,
                    dividerHeight / 2.0,
                    dividerWidth / 2.0));
                if (coverage > 0.0)
                {
                    // Well below the text: a separator that competes with the readings has
                    // stopped being a separator.
                    canvas.Blend(x, y, color, coverage * alpha * kDividerAlpha);
                }
            }
        }
    }

    return true;
}

// --- condition illustration ------------------------------------------------------------

struct WeatherPalette
{
    COLORREF sky{};
    double skyAlpha{};
    COLORREF motif{};
    double motifAlpha{};
    double motifStrongAlpha{};
};

// The WwbWeatherSky* families, keyed the way the card's template selector keys them: one sky
// per weather family, not one per condition.
enum class SkyFamily : unsigned char
{
    Clear,
    Night,
    Cloudy,
    Wet,
    Snow,
    Storm,
};

SkyFamily ResolveSkyFamily(const EntryIcon icon)
{
    switch (icon)
    {
    case EntryIcon::ClearNight:
    case EntryIcon::PartlyCloudyNight:
        return SkyFamily::Night;
    case EntryIcon::Cloudy:
    case EntryIcon::Fog:
        return SkyFamily::Cloudy;
    case EntryIcon::Drizzle:
    case EntryIcon::Rain:
        return SkyFamily::Wet;
    case EntryIcon::Snow:
        return SkyFamily::Snow;
    case EntryIcon::Thunderstorm:
        return SkyFamily::Storm;
    default:
        return SkyFamily::Clear;
    }
}

bool ResolveWeatherPalette(
    const EntryIcon icon,
    const EntryTheme& theme,
    WeatherPalette& palette)
{
    // High contrast draws no illustration: every WwbWeather* brush is Transparent there, and
    // a tint is exactly the kind of colour-only signal that theme exists to remove.
    if (theme.highContrast || !HasBackdrop(icon))
    {
        return false;
    }

    // WwbWeatherSky*Brush: the Default dictionary, then the Light one. The alphas are the
    // card's unchanged - the illustration sits on the capsule the way the card's sits on the
    // card surface, so it needs the same weight to read as the same material.
    struct SkyTone
    {
        COLORREF darkStrip;
        double darkAlpha;
        COLORREF lightStrip;
        double lightAlpha;
    };

    SkyTone tone{};
    switch (ResolveSkyFamily(icon))
    {
    case SkyFamily::Night:
        tone = {RGB(0x4C, 0x5D, 0xA8), 0.24, RGB(0x3B, 0x4C, 0x96), 0.20};
        break;
    case SkyFamily::Cloudy:
        tone = {RGB(0x89, 0x96, 0xA6), 0.20, RGB(0x6E, 0x7C, 0x8C), 0.18};
        break;
    case SkyFamily::Wet:
        tone = {RGB(0x54, 0x80, 0xA8), 0.22, RGB(0x3E, 0x6E, 0x96), 0.20};
        break;
    case SkyFamily::Snow:
        tone = {RGB(0x9C, 0xC4, 0xE4), 0.20, RGB(0x7F, 0xAE, 0xD4), 0.20};
        break;
    case SkyFamily::Storm:
        tone = {RGB(0x5B, 0x54, 0x88), 0.26, RGB(0x4A, 0x42, 0x76), 0.22};
        break;
    case SkyFamily::Clear:
    default:
        tone = {RGB(0x4A, 0xA3, 0xE8), 0.22, RGB(0x2E, 0x8B, 0xD8), 0.20};
        break;
    }

    if (theme.darkStrip)
    {
        palette.sky = tone.darkStrip;
        palette.skyAlpha = tone.darkAlpha;
        // WwbWeatherMotifBrush and WwbWeatherMotifStrongBrush, Default dictionary.
        palette.motif = RGB(0xFF, 0xFF, 0xFF);
        palette.motifAlpha = 0.10;
        palette.motifStrongAlpha = 0.20;
    }
    else
    {
        palette.sky = tone.lightStrip;
        palette.skyAlpha = tone.lightAlpha;
        palette.motif = RGB(0x17, 0x3A, 0x5E);
        palette.motifAlpha = 0.12;
        palette.motifStrongAlpha = 0.22;
    }

    return true;
}

// The falling element, staggered so three marks do not read as a comb.
void AddBackdropFall(IconMask& mask, const int count, const bool asDots)
{
    constexpr double kStart = 0.40;
    constexpr double kStep = 0.15;
    for (int index = 0; index < count; ++index)
    {
        const double x = kStart + kStep * static_cast<double>(index);
        const double y = 0.76 - (index % 2 == 0 ? 0.0 : 0.06);
        if (asDots)
        {
            AddUnitCircle(mask, x, y, 0.042);
        }
        else
        {
            AddUnitRoundedRect(mask, x, y, 0.058, 0.15, 0.029);
        }
    }
}

// Composes the illustration into two coverage masks, one per motif weight, and reports
// whether the strong one belongs in front. The card draws the sun and the moon behind their
// cloud and the bolt in front of it; the entry has to keep that reading.
bool BuildBackdropArt(const EntryIcon icon, IconMask& regular, IconMask& strong)
{
    switch (icon)
    {
    case EntryIcon::ClearDay:
        AddUnitCircle(strong, 0.60, 0.56, 0.30);
        break;
    case EntryIcon::ClearNight:
        AddCrescent(strong, 0.58, 0.54, 0.32);
        break;
    case EntryIcon::PartlyCloudyDay:
        AddUnitCircle(strong, 0.46, 0.34, 0.19);
        AddCloudAt(regular, 0.62, 0.64, 0.88);
        break;
    case EntryIcon::PartlyCloudyNight:
        AddCrescent(strong, 0.44, 0.33, 0.20);
        AddCloudAt(regular, 0.62, 0.64, 0.88);
        break;
    case EntryIcon::Cloudy:
        AddCloudAt(regular, 0.62, 0.62, 0.92);
        AddCloudAt(regular, 0.38, 0.38, 0.62);
        break;
    case EntryIcon::Fog:
        AddCloudAt(regular, 0.58, 0.44, 0.86);
        AddUnitRoundedRect(regular, 0.56, 0.74, 0.56, 0.06, 0.03);
        AddUnitRoundedRect(regular, 0.64, 0.88, 0.40, 0.06, 0.03);
        break;
    case EntryIcon::Drizzle:
    case EntryIcon::Rain:
        // Drizzle and rain share one illustration, exactly as they share one card template:
        // a drawing this size cannot honestly show the difference.
        AddCloudAt(regular, 0.58, 0.42, 0.86);
        AddBackdropFall(regular, 3, false);
        break;
    case EntryIcon::Snow:
        AddCloudAt(regular, 0.58, 0.42, 0.86);
        AddBackdropFall(regular, 3, true);
        break;
    case EntryIcon::Thunderstorm:
    {
        AddCloudAt(regular, 0.56, 0.40, 0.86);
        constexpr double kSourceX[] = {0.62, 0.38, 0.51, 0.34, 0.66, 0.51};
        constexpr double kSourceY[] = {0.62, 0.90, 0.90, 1.10, 0.83, 0.83};
        double boltX[std::size(kSourceX)]{};
        double boltY[std::size(kSourceY)]{};
        for (size_t index = 0; index < std::size(kSourceX); ++index)
        {
            boltX[index] = 0.60 + (kSourceX[index] - 0.50) * 0.72;
            boltY[index] = 0.76 + (kSourceY[index] - 0.86) * 0.72;
        }

        AddUnitPolygon(
            strong,
            boltX,
            boltY,
            static_cast<int>(std::size(kSourceX)));
        return true;
    }
    default:
        break;
    }

    return false;
}

// Paints the sky wash over the whole capsule and the motif into the trailing room reserved
// for it, both clipped to the capsule so the artwork bleeds off the closing cap instead of
// stopping at an invisible box.
void ComposeBackdrop(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const EntryTheme& theme,
    const EntryVisualState& state)
{
    WeatherPalette palette{};
    if (!ResolveWeatherPalette(request.icon, theme, palette))
    {
        return;
    }

    const CapsuleShape shape = ResolveCapsuleShape(request.capsule, state);

    for (int y = 0; y < canvas.height; ++y)
    {
        const double pixelY = static_cast<double>(y) + 0.5;
        for (int x = 0; x < canvas.width; ++x)
        {
            const double coverage =
                shape.CoverageAt(static_cast<double>(x) + 0.5, pixelY);
            if (coverage > 0.0)
            {
                canvas.Blend(x, y, palette.sky, coverage * palette.skyAlpha);
            }
        }
    }

    const double unit = RectHeightOf(request.capsule) * kMotifUnitScale;
    if (unit < 8.0)
    {
        return;
    }

    const double boxRight =
        static_cast<double>(request.capsule.right) + unit * kMotifOverflowX;
    const double boxBottom =
        static_cast<double>(request.capsule.bottom) + unit * kMotifOverflowY;
    const double boxLeft = boxRight - unit;
    const double boxTop = boxBottom - unit;

    // The masks cover only the motif box clipped to the canvas, so the illustration costs a
    // small buffer per frame rather than a second full-canvas one.
    const int left = std::max(0, static_cast<int>(std::floor(boxLeft)) - 2);
    const int top = std::max(0, static_cast<int>(std::floor(boxTop)) - 2);
    const int right =
        std::min(canvas.width, static_cast<int>(std::ceil(boxRight)) + 2);
    const int bottom =
        std::min(canvas.height, static_cast<int>(std::ceil(boxBottom)) + 2);
    if (right <= left || bottom <= top)
    {
        return;
    }

    const int regionWidth = right - left;
    const int regionHeight = bottom - top;

    IconMask regular{};
    IconMask strong{};
    for (IconMask* const mask : {&regular, &strong})
    {
        mask->width = regionWidth;
        mask->height = regionHeight;
        mask->coverage.assign(
            static_cast<size_t>(regionWidth) * static_cast<size_t>(regionHeight),
            0.0);
        mask->unit = unit;
        mask->originX = boxLeft - static_cast<double>(left);
        mask->originY = boxTop - static_cast<double>(top);
    }

    const bool strongInFront = BuildBackdropArt(request.icon, regular, strong);

    for (int y = 0; y < regionHeight; ++y)
    {
        const int canvasY = top + y;
        const double pixelY = static_cast<double>(canvasY) + 0.5;
        for (int x = 0; x < regionWidth; ++x)
        {
            const size_t index =
                static_cast<size_t>(y) * static_cast<size_t>(regionWidth) +
                    static_cast<size_t>(x);
            const double regularCoverage = regular.coverage[index];
            const double strongCoverage = strong.coverage[index];
            if (regularCoverage <= 0.0 && strongCoverage <= 0.0)
            {
                continue;
            }

            const int canvasX = left + x;
            const double clip =
                shape.CoverageAt(static_cast<double>(canvasX) + 0.5, pixelY);
            if (clip <= 0.0)
            {
                continue;
            }

            const auto blend = [&](const double coverage, const double alpha)
            {
                if (coverage > 0.0)
                {
                    canvas.Blend(
                        canvasX,
                        canvasY,
                        palette.motif,
                        coverage * alpha * clip);
                }
            };

            if (strongInFront)
            {
                blend(regularCoverage, palette.motifAlpha);
                blend(strongCoverage, palette.motifStrongAlpha);
            }
            else
            {
                blend(strongCoverage, palette.motifStrongAlpha);
                blend(regularCoverage, palette.motifAlpha);
            }
        }
    }
}

void ComposeIndicator(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const EntryTheme& theme,
    const EntryVisualState& state)
{
    if (request.compact)
    {
        return;
    }

    const double active = Clamp01(state.active);
    const double scale = 1.0 - (1.0 - kPressScale) * Clamp01(state.pressed);
    const double width = static_cast<double>(
        ScaleLogical(kEntryIndicatorWidthLogical, request.dpi));
    const double height = Lerp(
        static_cast<double>(
            ScaleLogical(kEntryIndicatorRestHeightLogical, request.dpi)),
        static_cast<double>(
            ScaleLogical(kEntryIndicatorActiveHeightLogical, request.dpi)),
        active) * scale;

    const double centerY =
        (static_cast<double>(request.capsule.top) +
            static_cast<double>(request.capsule.bottom)) / 2.0;
    const double capsuleCenterX =
        (static_cast<double>(request.capsule.left) +
            static_cast<double>(request.capsule.right)) / 2.0;
    const double restCenterX = static_cast<double>(request.capsule.left) +
        static_cast<double>(ScaleLogical(kEntryLeadingPaddingLogical, request.dpi)) +
        width / 2.0;
    // The indicator rides the same press scale as the capsule, around the capsule's own
    // centre, so nothing slides relative to anything else.
    const double centerX = capsuleCenterX + (restCenterX - capsuleCenterX) * scale;

    const COLORREF color = theme.highContrast
        ? MixColor(theme.highContrastText, theme.highContrastActiveText, active)
        : theme.accent;
    const double alpha = theme.highContrast
        ? 1.0
        : Lerp(kIndicatorRestAlpha, 1.0, active);

    for (int y = 0; y < canvas.height; ++y)
    {
        const double pixelY = static_cast<double>(y) + 0.5;
        if (std::abs(pixelY - centerY) > height / 2.0 + 1.5)
        {
            continue;
        }

        for (int x = 0; x < canvas.width; ++x)
        {
            const double pixelX = static_cast<double>(x) + 0.5;
            if (std::abs(pixelX - centerX) > width / 2.0 + 1.5)
            {
                continue;
            }

            const double coverage = CoverageFromDistance(RoundedRectDistance(
                pixelX,
                pixelY,
                centerX,
                centerY,
                width / 2.0,
                height / 2.0,
                width / 2.0));
            if (coverage > 0.0)
            {
                canvas.Blend(x, y, color, coverage * alpha);
            }
        }
    }
}

bool ComposeEntryBitmap(
    const EntryRenderRequest& request,
    const EntryTheme& theme,
    const EntryVisualState& state,
    std::vector<BYTE>& bgra)
{
    const int width = static_cast<int>(request.windowSize.cx);
    const int height = static_cast<int>(request.windowSize.cy);
    if (width <= 0 || height <= 0)
    {
        return false;
    }

    bgra.assign(static_cast<size_t>(width) * static_cast<size_t>(height) * 4, 0);
    Canvas canvas{bgra.data(), width, height};

    // An empty main capsule means "draw only the hardware capsule" - the preview sheet and the
    // composition smoke both review that one on its own. Guarding here rather than in each
    // pass keeps the indicator from landing at the window origin.
    const bool hasMain = RectWidthOf(request.capsule) > 0.0 &&
        RectHeightOf(request.capsule) > 0.0;
    if (hasMain)
    {
        ComposeCapsule(canvas, request, request.capsule, theme, state);
        // The illustration sits on the capsule, the way the card's sits on the card surface.
        // Under the neutral fill both the sky and the motif came out diluted to a grey smudge.
        ComposeBackdrop(canvas, request, theme, state);
        ComposeIndicator(canvas, request, theme, state);
    }

    // The hardware capsule shares the interaction state rather than lighting independently:
    // it is the same window and the same click target, and two capsules highlighting
    // separately would suggest two controls that do two different things.
    const bool hasMonitor = request.hasMonitorCapsule &&
        !request.segments.empty() &&
        RectWidthOf(request.monitorCapsule) > 0.0 &&
        RectHeightOf(request.monitorCapsule) > 0.0;
    if (hasMonitor)
    {
        ComposeCapsule(canvas, request, request.monitorCapsule, theme, state);
    }

    const COLORREF textColor = theme.highContrast
        ? MixColor(
            theme.highContrastText,
            theme.highContrastActiveText,
            Clamp01(state.active))
        : theme.text;
    const double textAlpha = theme.highContrast
        ? 1.0
        : Lerp(theme.textAlpha, theme.activeTextAlpha, Clamp01(state.active));
    if (hasMain)
    {
        ComposeIcon(canvas, request, textColor, textAlpha);
        if (!ComposeText(canvas, request, textColor, textAlpha))
        {
            return false;
        }
    }

    return !hasMonitor ||
        ComposeSegments(canvas, request, textColor, textAlpha);
}
}

EntryIcon ParseEntryIcon(const std::string_view conditionIconId)
{
    // The tokens are WeatherConditionContract and SystemMonitorContract values; keep them in
    // one place on this side too so a new one is a single edit rather than a scattered one.
    if (conditionIconId == "clear-day")
    {
        return EntryIcon::ClearDay;
    }
    if (conditionIconId == "clear-night")
    {
        return EntryIcon::ClearNight;
    }
    if (conditionIconId == "partly-cloudy-day")
    {
        return EntryIcon::PartlyCloudyDay;
    }
    if (conditionIconId == "partly-cloudy-night")
    {
        return EntryIcon::PartlyCloudyNight;
    }
    if (conditionIconId == "cloudy")
    {
        return EntryIcon::Cloudy;
    }
    if (conditionIconId == "fog")
    {
        return EntryIcon::Fog;
    }
    if (conditionIconId == "drizzle")
    {
        return EntryIcon::Drizzle;
    }
    if (conditionIconId == "rain")
    {
        return EntryIcon::Rain;
    }
    if (conditionIconId == "snow")
    {
        return EntryIcon::Snow;
    }
    if (conditionIconId == "thunderstorm")
    {
        return EntryIcon::Thunderstorm;
    }

    // SystemMonitorContract icon tokens.
    if (conditionIconId == "cpu")
    {
        return EntryIcon::Cpu;
    }
    if (conditionIconId == "memory")
    {
        return EntryIcon::Memory;
    }
    if (conditionIconId == "gpu")
    {
        return EntryIcon::Gpu;
    }
    if (conditionIconId == "disk")
    {
        return EntryIcon::Disk;
    }
    if (conditionIconId == "net-up")
    {
        return EntryIcon::NetworkUp;
    }
    if (conditionIconId == "net-down")
    {
        return EntryIcon::NetworkDown;
    }
    if (conditionIconId == "fan")
    {
        return EntryIcon::Fan;
    }

    return EntryIcon::Unknown;
}

EntryTheme QueryEntryTheme()
{
    EntryTheme theme{};

    HIGHCONTRASTW highContrast{};
    highContrast.cbSize = sizeof(highContrast);
    if (SystemParametersInfoW(
            SPI_GETHIGHCONTRAST,
            sizeof(highContrast),
            &highContrast,
            0) != FALSE)
    {
        theme.highContrast = (highContrast.dwFlags & HCF_HIGHCONTRASTON) != 0;
    }

    // The taskbar follows SystemUsesLightTheme, not the per-app setting. Windows writes the
    // value on every install; if it is somehow missing, light is the shipped default.
    DWORD lightTheme = 1;
    DWORD valueSize = sizeof(lightTheme);
    if (RegGetValueW(
            HKEY_CURRENT_USER,
            kPersonalizeKey,
            kSystemUsesLightThemeValue,
            RRF_RT_REG_DWORD,
            nullptr,
            &lightTheme,
            &valueSize) != ERROR_SUCCESS)
    {
        lightTheme = 1;
    }
    theme.darkStrip = lightTheme == 0;

    theme.accent = GetSysColor(COLOR_HIGHLIGHT);
    theme.highContrastFace = GetSysColor(COLOR_BTNFACE);
    theme.highContrastActiveFace = GetSysColor(COLOR_HIGHLIGHT);
    theme.highContrastText = GetSysColor(COLOR_BTNTEXT);
    theme.highContrastActiveText = GetSysColor(COLOR_HIGHLIGHTTEXT);
    theme.highContrastBorder = GetSysColor(COLOR_WINDOWTEXT);

    if (theme.darkStrip)
    {
        theme.surfaceTint = RGB(255, 255, 255);
        theme.text = RGB(255, 255, 255);
        theme.restAlpha = 0.10;
        theme.hoverAlpha = 0.20;
        theme.pressedAlpha = 0.26;
        theme.activeAlpha = 0.24;
        theme.textAlpha = 0.92;
        theme.activeTextAlpha = 1.0;
    }
    else
    {
        // A dark tint over a light strip carries far less perceived contrast per unit of
        // alpha than a light tint over a dark one, so the light steps are larger.
        theme.surfaceTint = RGB(0, 0, 0);
        theme.text = RGB(16, 16, 16);
        theme.restAlpha = 0.07;
        theme.hoverAlpha = 0.15;
        theme.pressedAlpha = 0.21;
        theme.activeAlpha = 0.20;
        theme.textAlpha = 0.88;
        theme.activeTextAlpha = 1.0;
    }

    return theme;
}

bool IsReducedMotionPreferred()
{
    BOOL clientAreaAnimation = TRUE;
    if (SystemParametersInfoW(
            SPI_GETCLIENTAREAANIMATION,
            0,
            &clientAreaAnimation,
            0) == FALSE)
    {
        return false;
    }

    return clientAreaAnimation == FALSE;
}

void EntryVisualAnimator::SetTarget(const EntryVisualState& target) noexcept
{
    _target = target;
}

void EntryVisualAnimator::SnapToTarget() noexcept
{
    _value = _target;
    _velocity = EntryVisualState{};
}

bool EntryVisualAnimator::IsSettled() const noexcept
{
    const auto settled = [](
        const double value,
        const double target,
        const double velocity)
    {
        return std::abs(value - target) <= kSettleValueEpsilon &&
            std::abs(velocity) <= kSettleVelocityEpsilon;
    };

    return settled(_value.hover, _target.hover, _velocity.hover) &&
        settled(_value.pressed, _target.pressed, _velocity.pressed) &&
        settled(_value.active, _target.active, _velocity.active);
}

bool EntryVisualAnimator::Advance(
    const double seconds,
    const bool reducedMotion) noexcept
{
    if (IsSettled())
    {
        SnapToTarget();
        return false;
    }

    const double step = std::clamp(seconds, 0.001, 0.05);
    if (reducedMotion)
    {
        // Reduced motion keeps a short cross-fade and drops every spring. The press scale
        // is driven by these same channels, so collapsing them removes the scale too.
        const double progress = 1.0 - std::exp(-step / kReducedMotionTimeConstant);
        _value.hover = Lerp(_value.hover, _target.hover, progress);
        _value.pressed = Lerp(_value.pressed, _target.pressed, progress);
        _value.active = Lerp(_value.active, _target.active, progress);
        _velocity = EntryVisualState{};
    }
    else
    {
        const auto advance =
            [step](double& value, const double target, double& velocity)
        {
            const double displacement = value - target;
            const double decay = std::exp(-kSpringAngularFrequency * step);
            const double velocityTerm =
                velocity + kSpringAngularFrequency * displacement;
            const double nextDisplacement =
                (displacement + velocityTerm * step) * decay;
            velocity =
                (velocity - kSpringAngularFrequency * velocityTerm * step) * decay;
            value = target + nextDisplacement;
        };

        advance(_value.hover, _target.hover, _velocity.hover);
        advance(_value.pressed, _target.pressed, _velocity.pressed);
        advance(_value.active, _target.active, _velocity.active);
    }

    if (IsSettled())
    {
        SnapToTarget();
        return false;
    }

    return true;
}

int MeasureEntryContentWidthLogical(
    const std::wstring& text,
    const UINT dpi,
    const bool compact,
    const EntryIcon icon)
{
    if (compact)
    {
        return 0;
    }

    const HDC screen = GetDC(nullptr);
    if (screen == nullptr)
    {
        return 0;
    }

    HFONT font = CreateEntryFont(
        ScaleLogical(kEntryFontSizeLogical, dpi),
        EntryTypeRole::Capsule);
    HGDIOBJ previousFont = SelectObject(screen, font);
    SIZE extent{};
    const bool measured = GetTextExtentPoint32W(
        screen,
        text.c_str(),
        static_cast<int>(text.size()),
        &extent) != FALSE;
    SelectObject(screen, previousFont);
    DeleteObject(font);
    ReleaseDC(nullptr, screen);

    if (!measured)
    {
        return 0;
    }

    return ToLogical(extent.cx, dpi) +
        LeadingWidthLogical(icon) +
        TrailingWidthLogical(icon);
}

int MeasureEntrySegmentsWidthLogical(
    const std::vector<EntrySegment>& segments,
    const UINT dpi)
{
    if (segments.empty())
    {
        return 0;
    }

    // No indicator allowance: that one lives in the main capsule.
    int width = kEntryLeadingPaddingLogical + kEntryTrailingPaddingLogical;

    // Mirrors ResolveSegmentLayout exactly - both derive their columns from the same
    // quantized content measurement, so the reserved capsule and the painted strip agree.
    const std::vector<int> columnWidthsLogical =
        ResolveSegmentColumnWidthsLogical(segments, dpi);
    const size_t count = segments.size();
    for (size_t first = 0; first < count; first += kEntrySegmentRowsPerColumn)
    {
        const size_t past = std::min(first + kEntrySegmentRowsPerColumn, count);
        width += columnWidthsLogical[first / kEntrySegmentRowsPerColumn];
        if (past < count)
        {
            width += kEntrySegmentColumnGapLogical * 2 + kEntryDividerWidthLogical;
        }
    }

    return width;
}

bool IsPointInCapsule(const RECT& capsule, const POINT clientPoint) noexcept
{
    const double halfWidth = RectWidthOf(capsule) / 2.0;
    const double halfHeight = RectHeightOf(capsule) / 2.0;
    if (halfWidth <= 0.0 || halfHeight <= 0.0)
    {
        return false;
    }

    const double distance = RoundedRectDistance(
        static_cast<double>(clientPoint.x) + 0.5,
        static_cast<double>(clientPoint.y) + 0.5,
        (static_cast<double>(capsule.left) +
            static_cast<double>(capsule.right)) / 2.0,
        (static_cast<double>(capsule.top) +
            static_cast<double>(capsule.bottom)) / 2.0,
        halfWidth,
        halfHeight,
        std::min(halfWidth, halfHeight));
    return distance <= 0.0;
}

bool RenderEntry(
    const HWND window,
    const EntryRenderRequest& request,
    const EntryTheme& theme,
    const EntryVisualState& state,
    std::wstring& error)
{
    error.clear();
    if (window == nullptr)
    {
        error = L"entry render target is unavailable";
        return false;
    }

    std::vector<BYTE> pixels;
    if (!ComposeEntryBitmap(request, theme, state, pixels))
    {
        error = L"entry bitmap composition failed";
        return false;
    }

    DibSurface surface;
    if (!surface.Create(
            static_cast<int>(request.windowSize.cx),
            static_cast<int>(request.windowSize.cy)))
    {
        error = L"CreateDIBSection for the entry failed";
        return false;
    }

    std::copy(pixels.begin(), pixels.end(), surface.bits);

    const HDC screen = GetDC(nullptr);
    if (screen == nullptr)
    {
        error = L"GetDC(nullptr) failed";
        return false;
    }

    POINT source{0, 0};
    SIZE size = request.windowSize;
    BLENDFUNCTION blend{};
    blend.BlendOp = AC_SRC_OVER;
    blend.SourceConstantAlpha = 255;
    blend.AlphaFormat = AC_SRC_ALPHA;

    const BOOL updated = UpdateLayeredWindow(
        window,
        screen,
        nullptr,
        &size,
        surface.deviceContext,
        &source,
        0,
        &blend,
        ULW_ALPHA);
    const DWORD lastError = GetLastError();
    ReleaseDC(nullptr, screen);

    if (updated == FALSE)
    {
        error = L"UpdateLayeredWindow failed: " + std::to_wstring(lastError);
        return false;
    }

    return true;
}

namespace
{
// Minimal 24-bit bottom-up BMP writer. A preview sheet needs no alpha and no dependency on
// an image codec, so the file is assembled by hand.
bool WriteBitmap24(
    const std::wstring& path,
    const std::vector<BYTE>& bgr,
    const int width,
    const int height,
    std::wstring& failure)
{
    const int rowBytes = width * 3;
    const int padding = (4 - (rowBytes % 4)) % 4;
    const int strideBytes = rowBytes + padding;
    const DWORD pixelBytes = static_cast<DWORD>(strideBytes) * static_cast<DWORD>(height);

    BITMAPFILEHEADER fileHeader{};
    fileHeader.bfType = 0x4D42;
    fileHeader.bfOffBits = sizeof(BITMAPFILEHEADER) + sizeof(BITMAPINFOHEADER);
    fileHeader.bfSize = fileHeader.bfOffBits + pixelBytes;

    BITMAPINFOHEADER infoHeader{};
    infoHeader.biSize = sizeof(infoHeader);
    infoHeader.biWidth = width;
    infoHeader.biHeight = height;
    infoHeader.biPlanes = 1;
    infoHeader.biBitCount = 24;
    infoHeader.biCompression = BI_RGB;
    infoHeader.biSizeImage = pixelBytes;

    const HANDLE file = CreateFileW(
        path.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        CREATE_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        failure = L"could not create the preview file: " +
            std::to_wstring(GetLastError());
        return false;
    }

    bool ok = true;
    DWORD written = 0;
    ok = ok && WriteFile(file, &fileHeader, sizeof(fileHeader), &written, nullptr) != FALSE;
    ok = ok && WriteFile(file, &infoHeader, sizeof(infoHeader), &written, nullptr) != FALSE;

    const std::vector<BYTE> pad(static_cast<size_t>(padding), 0);
    for (int y = height - 1; y >= 0 && ok; --y)
    {
        const BYTE* const row = bgr.data() +
            static_cast<size_t>(y) * static_cast<size_t>(rowBytes);
        ok = WriteFile(file, row, static_cast<DWORD>(rowBytes), &written, nullptr) != FALSE;
        if (ok && padding > 0)
        {
            ok = WriteFile(
                file,
                pad.data(),
                static_cast<DWORD>(padding),
                &written,
                nullptr) != FALSE;
        }
    }

    CloseHandle(file);
    if (!ok)
    {
        failure = L"writing the preview file failed";
    }

    return ok;
}

// Composites a premultiplied tile over a flat background into the 24-bit sheet.
void BlitTile(
    std::vector<BYTE>& sheet,
    const int sheetWidth,
    const int destinationX,
    const int destinationY,
    const std::vector<BYTE>& tile,
    const int tileWidth,
    const int tileHeight,
    const COLORREF background)
{
    for (int y = 0; y < tileHeight; ++y)
    {
        for (int x = 0; x < tileWidth; ++x)
        {
            const BYTE* const source = tile.data() +
                (static_cast<size_t>(y) * static_cast<size_t>(tileWidth) +
                    static_cast<size_t>(x)) * 4;
            const double alpha = static_cast<double>(source[3]) / 255.0;
            const size_t index =
                (static_cast<size_t>(destinationY + y) * static_cast<size_t>(sheetWidth) +
                    static_cast<size_t>(destinationX + x)) * 3;
            if (index + 2 >= sheet.size())
            {
                continue;
            }

            const int channels[3] = {
                GetBValue(background),
                GetGValue(background),
                GetRValue(background),
            };
            for (int channel = 0; channel < 3; ++channel)
            {
                const double premultiplied = static_cast<double>(source[channel]) / 255.0;
                const double result = premultiplied +
                    static_cast<double>(channels[channel]) / 255.0 * (1.0 - alpha);
                sheet[index + static_cast<size_t>(channel)] =
                    static_cast<BYTE>(std::lround(Clamp01(result) * 255.0));
            }
        }
    }
}
}

bool WriteEntryPreviewSheet(const std::wstring& path, std::wstring& failure)
{
    failure.clear();

    const EntryIcon icons[] = {
        EntryIcon::ClearDay,
        EntryIcon::ClearNight,
        EntryIcon::PartlyCloudyDay,
        EntryIcon::PartlyCloudyNight,
        EntryIcon::Cloudy,
        EntryIcon::Fog,
        EntryIcon::Drizzle,
        EntryIcon::Rain,
        EntryIcon::Snow,
        EntryIcon::Thunderstorm,
        EntryIcon::Unknown,
    };
    constexpr int kIconCount = static_cast<int>(std::size(icons));

    // Two columns: the entry over a dark strip and over a light one, at 192 dpi so the
    // artwork is reviewed at the density it actually renders at on the reference machine.
    constexpr UINT kDpi = 192;
    const int tileWidth = MulDiv(140, static_cast<int>(kDpi), 96);
    const int tileHeight = MulDiv(40, static_cast<int>(kDpi), 96);
    const int gap = MulDiv(8, static_cast<int>(kDpi), 96);
    // Two hardware strips, one per taskbar theme, with the glyph set the monitor actually
    // uses and values in the shapes the broker composes. Eight readings is the cap the
    // contract allows and what the shipped default asks for once every metric this machine
    // can read is turned on.
    // A plausible two-minute window per reading, so the sheet reviews the graph and the
    // number together rather than the number on an empty band.
    const auto previewHistory = [](const double base, const double swing)
    {
        std::vector<double> history;
        history.reserve(48);
        for (int index = 0; index < 48; ++index)
        {
            const double phase = static_cast<double>(index) / 6.0;
            history.push_back(Clamp01(base + swing * std::sin(phase)));
        }

        return history;
    };

    const std::vector<EntrySegment> monitorStrip = {
        EntrySegment{EntryIcon::Cpu, L"CPU 24%", previewHistory(0.24, 0.18)},
        EntrySegment{EntryIcon::Memory, L"MEM 61%", previewHistory(0.61, 0.04)},
        EntrySegment{EntryIcon::Gpu, L"GPU 8%", previewHistory(0.08, 0.07)},
        EntrySegment{EntryIcon::Gpu, L"VRAM 42%", previewHistory(0.42, 0.03)},
        EntrySegment{EntryIcon::Disk, L"DISK 12%", previewHistory(0.12, 0.11)},
        EntrySegment{EntryIcon::Disk, L"DISK 68%", previewHistory(0.68, 0.01)},
        EntrySegment{EntryIcon::NetworkDown, L"7.2 MB/s", previewHistory(0.45, 0.45)},
        EntrySegment{EntryIcon::NetworkUp, L"164 KB/s", previewHistory(0.2, 0.2)},
    };

    // The hardware strip is far wider than a weather chip, so it gets its own two rows at the
    // bottom of the sheet rather than being squeezed into the glyph column. Its width comes
    // from the same measurement the taskbar asks for, so the sheet shows the room the entry
    // actually claims instead of a figure picked for the sheet.
    const int stripWidth = MulDiv(
        MeasureEntrySegmentsWidthLogical(monitorStrip, kDpi),
        static_cast<int>(kDpi),
        96);
    // The pair as the taskbar actually shows it: the main capsule with a clock, the gap, and
    // the hardware capsule. Reviewing the readings alone hides the one thing this layout was
    // changed for - that they read as a separate instrument beside the entry, not inside it.
    const int mainWidth = MulDiv(
        MeasureEntryContentWidthLogical(L"9:41  2026/8/22", kDpi, false, EntryIcon::None),
        static_cast<int>(kDpi),
        96);
    const int capsuleGap = MulDiv(8, static_cast<int>(kDpi), 96);
    const int pairWidth = mainWidth + capsuleGap + stripWidth;
    const int sheetWidth = std::max(
        gap + (tileWidth + gap) * 2,
        gap + pairWidth + gap);
    const int sheetHeight =
        gap + (tileHeight + gap) * kIconCount + (tileHeight + gap) * 4;

    const COLORREF darkStrip1 = RGB(32, 34, 38);
    const COLORREF lightStrip = RGB(214, 224, 222);

    std::vector<BYTE> sheet(
        static_cast<size_t>(sheetWidth) * static_cast<size_t>(sheetHeight) * 3,
        0);
    for (int y = 0; y < sheetHeight; ++y)
    {
        for (int x = 0; x < sheetWidth; ++x)
        {
            const bool rightColumn = x > sheetWidth / 2;
            const COLORREF background = rightColumn ? lightStrip : darkStrip1;
            const size_t index =
                (static_cast<size_t>(y) * static_cast<size_t>(sheetWidth) +
                    static_cast<size_t>(x)) * 3;
            sheet[index] = GetBValue(background);
            sheet[index + 1] = GetGValue(background);
            sheet[index + 2] = GetRValue(background);
        }
    }

    for (int row = 0; row < kIconCount; ++row)
    {
        for (int column = 0; column < 2; ++column)
        {
            EntryTheme theme{};
            theme.darkStrip = column == 0;
            theme.accent = RGB(0, 120, 212);
            if (theme.darkStrip)
            {
                theme.surfaceTint = RGB(255, 255, 255);
                theme.text = RGB(255, 255, 255);
                theme.restAlpha = 0.10;
                theme.hoverAlpha = 0.20;
                theme.pressedAlpha = 0.26;
                theme.activeAlpha = 0.24;
                theme.textAlpha = 0.92;
                theme.activeTextAlpha = 1.0;
            }
            else
            {
                theme.surfaceTint = RGB(0, 0, 0);
                theme.text = RGB(16, 16, 16);
                theme.restAlpha = 0.07;
                theme.hoverAlpha = 0.15;
                theme.pressedAlpha = 0.21;
                theme.activeAlpha = 0.20;
                theme.textAlpha = 0.88;
                theme.activeTextAlpha = 1.0;
            }

            EntryRenderRequest request{};
            request.windowSize = SIZE{tileWidth, tileHeight};
            request.capsule = RECT{0, 0, tileWidth, tileHeight};
            request.dpi = kDpi;
            request.icon = icons[row];
            request.text = L"26" + std::wstring(1, static_cast<wchar_t>(0x00B0));

            std::vector<BYTE> tile;
            if (!ComposeEntryBitmap(request, theme, EntryVisualState{}, tile))
            {
                failure = L"preview composition failed";
                return false;
            }

            BlitTile(
                sheet,
                sheetWidth,
                gap + (tileWidth + gap) * column,
                gap + (tileHeight + gap) * row,
                tile,
                tileWidth,
                tileHeight,
                column == 0 ? darkStrip1 : lightStrip);
        }
    }

    for (int row = 0; row < 2; ++row)
    {
        const bool darkStrip = row == 0;
        EntryTheme theme{};
        theme.darkStrip = darkStrip;
        theme.accent = RGB(0, 120, 212);
        if (darkStrip)
        {
            theme.surfaceTint = RGB(255, 255, 255);
            theme.text = RGB(255, 255, 255);
            theme.restAlpha = 0.10;
            theme.hoverAlpha = 0.20;
            theme.pressedAlpha = 0.26;
            theme.activeAlpha = 0.24;
            theme.textAlpha = 0.92;
            theme.activeTextAlpha = 1.0;
        }
        else
        {
            theme.surfaceTint = RGB(0, 0, 0);
            theme.text = RGB(16, 16, 16);
            theme.restAlpha = 0.07;
            theme.hoverAlpha = 0.15;
            theme.pressedAlpha = 0.21;
            theme.activeAlpha = 0.20;
            theme.textAlpha = 0.88;
            theme.activeTextAlpha = 1.0;
        }

        // Two rows per theme: the hardware capsule on its own, so the readings can be judged
        // at full width, and then the pair exactly as the taskbar shows it.
        EntryRenderRequest strip{};
        strip.windowSize = SIZE{stripWidth, tileHeight};
        strip.capsule = RECT{};
        strip.monitorCapsule = RECT{0, 0, stripWidth, tileHeight};
        strip.hasMonitorCapsule = true;
        strip.dpi = kDpi;
        strip.segments = monitorStrip;

        std::vector<BYTE> tile;
        if (!ComposeEntryBitmap(strip, theme, EntryVisualState{}, tile))
        {
            failure = L"monitor strip composition failed";
            return false;
        }

        BlitTile(
            sheet,
            sheetWidth,
            gap,
            gap + (tileHeight + gap) * (kIconCount + row),
            tile,
            stripWidth,
            tileHeight,
            darkStrip ? darkStrip1 : lightStrip);

        EntryRenderRequest pair{};
        pair.windowSize = SIZE{pairWidth, tileHeight};
        pair.capsule = RECT{0, 0, mainWidth, tileHeight};
        pair.monitorCapsule = RECT{mainWidth + capsuleGap, 0, pairWidth, tileHeight};
        pair.hasMonitorCapsule = true;
        pair.dpi = kDpi;
        pair.text = L"9:41  2026/8/22";
        pair.segments = monitorStrip;

        std::vector<BYTE> pairTile;
        if (!ComposeEntryBitmap(pair, theme, EntryVisualState{}, pairTile))
        {
            failure = L"paired capsule composition failed";
            return false;
        }

        BlitTile(
            sheet,
            sheetWidth,
            gap,
            gap + (tileHeight + gap) * (kIconCount + 2 + row),
            pairTile,
            pairWidth,
            tileHeight,
            darkStrip ? darkStrip1 : lightStrip);
    }

    return WriteBitmap24(path, sheet, sheetWidth, sheetHeight, failure);
}

bool RunEntryVisualSmokeTest(std::wstring& failure)
{
    failure.clear();

    const auto describe = [](const wchar_t* const label, const UINT dpi)
    {
        return std::wstring(label) + L" at " +
            std::to_wstring(static_cast<int>(dpi)) + L" dpi";
    };

    for (const UINT dpi : {96U, 144U, 192U})
    {
        const int width = MulDiv(200, static_cast<int>(dpi), 96);
        const int height = MulDiv(40, static_cast<int>(dpi), 96);

        EntryRenderRequest request{};
        request.windowSize = SIZE{width, height};
        request.capsule = RECT{0, 0, width, height};
        request.dpi = dpi;
        request.text = L"12:34  2026/8/20";

        EntryTheme theme{};
        theme.darkStrip = true;
        theme.accent = RGB(0, 120, 212);
        theme.surfaceTint = RGB(255, 255, 255);
        theme.text = RGB(255, 255, 255);
        theme.restAlpha = 0.10;
        theme.hoverAlpha = 0.18;
        theme.pressedAlpha = 0.24;
        theme.activeAlpha = 0.24;
        theme.textAlpha = 0.92;
        theme.activeTextAlpha = 1.0;

        std::vector<BYTE> rest;
        if (!ComposeEntryBitmap(request, theme, EntryVisualState{}, rest))
        {
            failure = describe(L"rest composition failed", dpi);
            return false;
        }

        const auto alphaAt = [width](
            const std::vector<BYTE>& pixels,
            const int x,
            const int y)
        {
            return pixels[(static_cast<size_t>(y) * static_cast<size_t>(width) +
                static_cast<size_t>(x)) * 4 + 3];
        };

        // Premultiplication is what UpdateLayeredWindow requires. A channel above alpha
        // shows up as a bright halo rather than a hard failure, so assert it directly.
        for (size_t index = 0; index + 3 < rest.size(); index += 4)
        {
            const BYTE alpha = rest[index + 3];
            if (rest[index] > alpha ||
                rest[index + 1] > alpha ||
                rest[index + 2] > alpha)
            {
                failure = describe(L"a pixel is not premultiplied", dpi);
                return false;
            }
        }

        if (alphaAt(rest, 0, 0) != 0)
        {
            failure = describe(L"the capsule corner is not transparent", dpi);
            return false;
        }

        if (alphaAt(rest, width / 2, height / 2) == 0)
        {
            failure = describe(L"the capsule centre is empty", dpi);
            return false;
        }

        bool antialiased = false;
        for (int y = 0; y < height; ++y)
        {
            const BYTE alpha = alphaAt(rest, 0, y);
            if (alpha > 0 && alpha < 255)
            {
                antialiased = true;
                break;
            }
        }
        if (!antialiased)
        {
            failure = describe(L"the capsule edge has no antialiasing", dpi);
            return false;
        }

        // Hover has to be visibly stronger than rest. This is the only automated check
        // that catches a hover step tuned so gently it reads as no feedback at all.
        std::vector<BYTE> hovered;
        if (!ComposeEntryBitmap(request, theme, EntryVisualState{1, 0, 0}, hovered))
        {
            failure = describe(L"hover composition failed", dpi);
            return false;
        }

        const int restAlpha = alphaAt(rest, width - 8, height / 2);
        const int hoverAlpha = alphaAt(hovered, width - 8, height / 2);
        if (hoverAlpha - restAlpha < 12)
        {
            failure = describe(L"hover is not visibly stronger than rest", dpi);
            return false;
        }

        // The indicator has to grow into a bar, so the open state reads without colour.
        std::vector<BYTE> active;
        if (!ComposeEntryBitmap(request, theme, EntryVisualState{0, 0, 1}, active))
        {
            failure = describe(L"active composition failed", dpi);
            return false;
        }

        const int indicatorX = MulDiv(
            kEntryLeadingPaddingLogical + kEntryIndicatorWidthLogical / 2,
            static_cast<int>(dpi),
            96);
        const auto indicatorExtent = [&](const std::vector<BYTE>& pixels)
        {
            int count = 0;
            for (int y = 0; y < height; ++y)
            {
                if (alphaAt(pixels, indicatorX, y) > 96)
                {
                    ++count;
                }
            }

            return count;
        };

        if (indicatorExtent(active) <= indicatorExtent(rest))
        {
            failure = describe(
                L"the active indicator is not taller than the resting dot",
                dpi);
            return false;
        }

        // High contrast has to be fully opaque and bounded by the system border.
        EntryTheme contrast = theme;
        contrast.highContrast = true;
        contrast.highContrastFace = RGB(0, 0, 0);
        contrast.highContrastActiveFace = RGB(255, 255, 0);
        contrast.highContrastText = RGB(255, 255, 255);
        contrast.highContrastActiveText = RGB(0, 0, 0);
        contrast.highContrastBorder = RGB(255, 255, 255);

        std::vector<BYTE> opaque;
        if (!ComposeEntryBitmap(request, contrast, EntryVisualState{}, opaque))
        {
            failure = describe(L"high contrast composition failed", dpi);
            return false;
        }

        if (alphaAt(opaque, width / 2, height / 2) != 255)
        {
            failure = describe(L"high contrast is not opaque", dpi);
            return false;
        }
    }

    {
        // The floating badge is a circle with a centred glyph and no indicator.
        EntryRenderRequest compact{};
        compact.windowSize = SIZE{52, 52};
        compact.capsule = RECT{8, 8, 44, 44};
        compact.dpi = 96;
        compact.compact = true;
        compact.text = L"W";

        EntryTheme theme{};
        theme.darkStrip = true;
        theme.accent = RGB(0, 120, 212);
        theme.surfaceTint = RGB(255, 255, 255);
        theme.text = RGB(255, 255, 255);
        theme.restAlpha = 0.10;
        theme.hoverAlpha = 0.20;
        theme.pressedAlpha = 0.26;
        theme.activeAlpha = 0.24;
        theme.textAlpha = 0.92;
        theme.activeTextAlpha = 1.0;

        std::vector<BYTE> pixels;
        if (!ComposeEntryBitmap(compact, theme, EntryVisualState{}, pixels))
        {
            failure = L"the floating badge failed to compose";
            return false;
        }

        const auto alphaAt = [](const std::vector<BYTE>& buffer, const int x, const int y)
        {
            return buffer[(static_cast<size_t>(y) * 52 + static_cast<size_t>(x)) * 4 + 3];
        };

        if (alphaAt(pixels, 0, 0) != 0 || alphaAt(pixels, 26, 26) == 0)
        {
            failure = L"the floating badge is not a circle inside its padding";
            return false;
        }

        if (MeasureEntryContentWidthLogical(L"W", 96, true) != 0)
        {
            failure = L"the floating badge must not request an adaptive width";
            return false;
        }
    }

    {
        // Every condition glyph has to draw something, and each has to differ from the
        // others: a mapping that silently collapsed two conditions onto one drawing would
        // otherwise look perfectly fine until someone compared them side by side.
        const EntryIcon icons[] = {
            EntryIcon::ClearDay,
            EntryIcon::ClearNight,
            EntryIcon::PartlyCloudyDay,
            EntryIcon::PartlyCloudyNight,
            EntryIcon::Cloudy,
            EntryIcon::Fog,
            EntryIcon::Drizzle,
            EntryIcon::Rain,
            EntryIcon::Snow,
            EntryIcon::Thunderstorm,
            EntryIcon::Cpu,
            EntryIcon::Memory,
            EntryIcon::Gpu,
            EntryIcon::Disk,
            EntryIcon::NetworkUp,
            EntryIcon::NetworkDown,
            EntryIcon::Fan,
            EntryIcon::Unknown,
        };

        EntryTheme theme{};
        theme.darkStrip = true;
        theme.accent = RGB(0, 120, 212);
        theme.surfaceTint = RGB(255, 255, 255);
        theme.text = RGB(255, 255, 255);
        theme.restAlpha = 0.10;
        theme.hoverAlpha = 0.20;
        theme.pressedAlpha = 0.26;
        theme.activeAlpha = 0.24;
        theme.textAlpha = 0.92;
        theme.activeTextAlpha = 1.0;

        constexpr int kWidth = 200;
        constexpr int kHeight = 40;
        const RECT iconBox = ResolveIconRect(EntryRenderRequest{
            SIZE{kWidth, kHeight},
            RECT{0, 0, kWidth, kHeight},
            96,
            false,
            EntryIcon::ClearDay,
            {},
        });

        std::vector<std::vector<BYTE>> rendered;
        for (const EntryIcon icon : icons)
        {
            EntryRenderRequest request{};
            request.windowSize = SIZE{kWidth, kHeight};
            request.capsule = RECT{0, 0, kWidth, kHeight};
            request.dpi = 96;
            request.icon = icon;
            request.text = L"26";

            std::vector<BYTE> pixels;
            if (!ComposeEntryBitmap(request, theme, EntryVisualState{}, pixels))
            {
                failure = L"a condition glyph failed to compose";
                return false;
            }

            // Only the glyph box is compared, so the shared capsule and text cannot mask a
            // glyph that draws nothing.
            std::vector<BYTE> box;
            for (int y = iconBox.top; y < iconBox.bottom; ++y)
            {
                for (int x = iconBox.left; x < iconBox.right; ++x)
                {
                    box.push_back(pixels[
                        (static_cast<size_t>(y) * static_cast<size_t>(kWidth) +
                            static_cast<size_t>(x)) * 4 + 3]);
                }
            }

            bool hasInk = false;
            for (const BYTE alpha : box)
            {
                if (alpha > 40)
                {
                    hasInk = true;
                    break;
                }
            }

            if (!hasInk)
            {
                failure = L"a condition glyph drew nothing";
                return false;
            }

            for (const std::vector<BYTE>& previous : rendered)
            {
                if (previous == box)
                {
                    failure = L"two condition glyphs are drawn identically";
                    return false;
                }
            }

            rendered.push_back(std::move(box));
        }

        // The glyph and the illustration each have to claim their own width, otherwise one
        // of them would end up on top of the text.
        const int withoutIcon =
            MeasureEntryContentWidthLogical(L"26", 96, false, EntryIcon::None);
        const int withIcon =
            MeasureEntryContentWidthLogical(L"26", 96, false, EntryIcon::Rain);
        if (withIcon - withoutIcon !=
            kEntryIconSizeLogical + kEntryIconGapLogical + kEntryMotifWidthLogical)
        {
            failure = L"the condition glyph and illustration do not reserve their own width";
            return false;
        }

        // Unknown draws a glyph but no illustration, so it must not claim the motif room.
        const int withUnknown =
            MeasureEntryContentWidthLogical(L"26", 96, false, EntryIcon::Unknown);
        if (withUnknown - withoutIcon !=
            kEntryIconSizeLogical + kEntryIconGapLogical)
        {
            failure = L"an unknown condition reserved illustration room it never draws";
            return false;
        }

        if (ParseEntryIcon("rain") != EntryIcon::Rain ||
            ParseEntryIcon("clear-night") != EntryIcon::ClearNight ||
            ParseEntryIcon("not-a-condition") != EntryIcon::Unknown)
        {
            failure = L"condition token parsing is wrong";
            return false;
        }
    }

    {
        // The condition illustration: it has to colour the capsule, add motif ink of its own
        // in the room reserved for it, stay clipped to the capsule, and disappear entirely in
        // high contrast, where every WwbWeather* brush is Transparent.
        constexpr int kWidth = 200;
        constexpr int kHeight = 40;

        EntryTheme theme{};
        theme.darkStrip = true;
        theme.accent = RGB(0, 120, 212);
        theme.surfaceTint = RGB(255, 255, 255);
        theme.text = RGB(255, 255, 255);
        theme.restAlpha = 0.10;
        theme.hoverAlpha = 0.20;
        theme.pressedAlpha = 0.26;
        theme.activeAlpha = 0.24;
        theme.textAlpha = 0.92;
        theme.activeTextAlpha = 1.0;

        const auto render = [&](
            const EntryIcon icon,
            const EntryTheme& useTheme,
            std::vector<BYTE>& pixels)
        {
            EntryRenderRequest request{};
            request.windowSize = SIZE{kWidth, kHeight};
            request.capsule = RECT{0, 0, kWidth, kHeight};
            request.dpi = 96;
            request.icon = icon;
            request.text = L"26";
            return ComposeEntryBitmap(request, useTheme, EntryVisualState{}, pixels);
        };

        const auto channelAt = [](
            const std::vector<BYTE>& pixels,
            const int x,
            const int y,
            const int channel)
        {
            return static_cast<int>(pixels[
                (static_cast<size_t>(y) * static_cast<size_t>(kWidth) +
                    static_cast<size_t>(x)) * 4 + static_cast<size_t>(channel)]);
        };

        // The trailing motif box, restated from the same constants the compositor uses, and
        // a point inside the cloud every falling condition draws.
        constexpr double kUnit = static_cast<double>(kHeight) * kMotifUnitScale;
        constexpr double kBoxLeft =
            static_cast<double>(kWidth) + kUnit * kMotifOverflowX - kUnit;
        constexpr double kBoxTop =
            static_cast<double>(kHeight) + kUnit * kMotifOverflowY - kUnit;
        constexpr int kMotifX = static_cast<int>(kBoxLeft + 0.64 * kUnit);
        constexpr int kMotifY = static_cast<int>(kBoxTop + 0.46 * kUnit);
        static_assert(
            kMotifX > kWidth / 2 && kMotifX < kWidth &&
                kMotifY > 0 && kMotifY < kHeight,
            "the illustration sample has to land inside the entry's trailing motif room");

        std::vector<BYTE> neutral;
        std::vector<BYTE> rainy;
        if (!render(EntryIcon::Unknown, theme, neutral) ||
            !render(EntryIcon::Rain, theme, rainy))
        {
            failure = L"the condition illustration failed to compose";
            return false;
        }

        // Premultiplied BGRA: channel 0 is blue, channel 2 is red. The neutral capsule is a
        // white tint, so its channels match; a wet sky has to be visibly bluer than that.
        if (std::abs(channelAt(neutral, kWidth / 2, kMotifY, 0) -
                channelAt(neutral, kWidth / 2, kMotifY, 2)) > 2)
        {
            failure = L"the neutral capsule is not colour-free";
            return false;
        }

        if (channelAt(rainy, kWidth / 2, kMotifY, 0) -
                channelAt(rainy, kWidth / 2, kMotifY, 2) < 6)
        {
            failure = L"the condition illustration did not tint the capsule";
            return false;
        }

        // The motif has to add ink over the sky wash, not just repeat it.
        const int skyAlpha = channelAt(rainy, kWidth / 2, kMotifY, 3);
        const int motifAlpha = channelAt(rainy, kMotifX, kMotifY, 3);
        if (motifAlpha - skyAlpha < 10)
        {
            failure = L"the condition illustration drew no motif";
            return false;
        }

        if (channelAt(rainy, 0, 0, 3) != 0 ||
            channelAt(rainy, kWidth - 1, kHeight - 1, 3) != 0)
        {
            failure = L"the condition illustration escaped the capsule";
            return false;
        }

        EntryTheme contrast = theme;
        contrast.highContrast = true;
        contrast.highContrastFace = RGB(0, 0, 0);
        contrast.highContrastActiveFace = RGB(255, 255, 0);
        contrast.highContrastText = RGB(255, 255, 255);
        contrast.highContrastActiveText = RGB(0, 0, 0);
        contrast.highContrastBorder = RGB(255, 255, 255);

        std::vector<BYTE> opaque;
        if (!render(EntryIcon::Rain, contrast, opaque))
        {
            failure = L"the high contrast illustration failed to compose";
            return false;
        }

        if (channelAt(opaque, kWidth / 2, kMotifY, 0) !=
                channelAt(opaque, kWidth / 2, kMotifY, 2))
        {
            failure = L"high contrast kept the condition tint";
            return false;
        }

        if (!HasBackdrop(EntryIcon::Rain) ||
            HasBackdrop(EntryIcon::Unknown) ||
            HasBackdrop(EntryIcon::None))
        {
            failure = L"the illustration is claimed for a condition that has none";
            return false;
        }
    }

    {
        // The segmented strip: several readings, a hairline between neighbours, and a clean
        // end when the configured set is wider than the entry can be.
        EntryTheme theme{};
        theme.darkStrip = true;
        theme.accent = RGB(0, 120, 212);
        theme.surfaceTint = RGB(255, 255, 255);
        theme.text = RGB(255, 255, 255);
        theme.restAlpha = 0.10;
        theme.hoverAlpha = 0.20;
        theme.pressedAlpha = 0.26;
        theme.activeAlpha = 0.24;
        theme.textAlpha = 0.92;
        theme.activeTextAlpha = 1.0;

        constexpr int kWidth = 420;
        constexpr int kHeight = 40;

        const auto compose = [&](
            const std::vector<EntrySegment>& segments,
            std::vector<BYTE>& pixels)
        {
            EntryRenderRequest request{};
            request.windowSize = SIZE{kWidth, kHeight};
            request.capsule = RECT{};
            request.monitorCapsule = RECT{0, 0, kWidth, kHeight};
            request.hasMonitorCapsule = true;
            request.dpi = 96;
            request.segments = segments;
            return ComposeEntryBitmap(request, theme, EntryVisualState{}, pixels);
        };

        const auto alphaAt = [](const std::vector<BYTE>& pixels, const int x, const int y)
        {
            return pixels[(static_cast<size_t>(y) * static_cast<size_t>(kWidth) +
                static_cast<size_t>(x)) * 4 + 3];
        };

        const std::vector<EntrySegment> three = {
            EntrySegment{EntryIcon::Cpu, L"CPU 24%"},
            EntrySegment{EntryIcon::Memory, L"MEM 61%"},
            EntrySegment{EntryIcon::NetworkDown, L"7.2 MB/s"},
        };

        std::vector<BYTE> strip;
        if (!compose(three, strip))
        {
            failure = L"the segmented strip failed to compose";
            return false;
        }

        const auto hasInkInBand = [&](
            const std::vector<BYTE>& pixels,
            const int fromX,
            const int pastX,
            const int fromY,
            const int pastY)
        {
            for (int y = std::max(0, fromY); y < std::min(kHeight, pastY); ++y)
            {
                for (int x = std::max(0, fromX); x < std::min(kWidth, pastX); ++x)
                {
                    if (alphaAt(pixels, x, y) > 120)
                    {
                        return true;
                    }
                }
            }

            return false;
        };

        // Both rows have to carry ink. A layout that quietly collapsed back to a single line
        // would still light up every column, so sampling columns alone cannot see it.
        const int contentLeft = ScaleLogical(
            kEntryLeadingPaddingLogical +
                kEntryIndicatorWidthLogical +
                kEntryIndicatorGapLogical,
            96);
        const int contentRight =
            MeasureEntrySegmentsWidthLogical(three, 96) - kEntryTrailingPaddingLogical;
        const int centerY = kHeight / 2;

        if (!hasInkInBand(strip, contentLeft, contentRight, 0, centerY) ||
            !hasInkInBand(strip, contentLeft, contentRight, centerY, kHeight))
        {
            failure = L"the segmented strip did not fill both of its rows";
            return false;
        }

        // Nothing may be drawn past the room the taskbar actually reserves - the measured
        // width rounded up to the 4 DIP quantum. A measurement that undercounts by more than
        // a glyph's antialiasing shows up here as ink beyond that.
        const int reserved =
            ResolveMonitorWidthLogical(MeasureEntrySegmentsWidthLogical(three, 96));
        if (hasInkInBand(strip, reserved, kWidth, 0, kHeight))
        {
            failure = L"the segmented strip drew past its reserved width";
            return false;
        }

        // The second reading shares a column with the first, so an identical pair must claim
        // exactly the width one of them does. This is the two-row packing stated as a number
        // rather than as pixels, and it is what a return to one row would break first.
        const std::vector<EntrySegment> onePair = {
            EntrySegment{EntryIcon::Cpu, L"CPU 24%"},
            EntrySegment{EntryIcon::Cpu, L"CPU 24%"},
        };
        if (MeasureEntrySegmentsWidthLogical(onePair, 96) !=
            MeasureEntrySegmentsWidthLogical(
                {EntrySegment{EntryIcon::Cpu, L"CPU 24%"}},
                96))
        {
            failure = L"a second reading did not share the first reading's column";
            return false;
        }

        // Digit flicker - the same character count with different digits, or the wide
        // reading swapping rows within its column - must not move a slot: the slack and the
        // quantum exist to absorb it. Growth is different and asserted separately below.
        const std::vector<EntrySegment> changingRates = {
            EntrySegment{EntryIcon::Cpu, L"CPU 24%"},
            EntrySegment{EntryIcon::Memory, L"MEM 61%"},
            EntrySegment{EntryIcon::NetworkDown, L"131.2 KB/s"},
            EntrySegment{EntryIcon::NetworkUp, L"999.9 MB/s"},
            EntrySegment{EntryIcon::Gpu, L"GPU 8%"},
        };
        const std::vector<EntrySegment> reversedRates = {
            EntrySegment{EntryIcon::Cpu, L"CPU 42%"},
            EntrySegment{EntryIcon::Memory, L"MEM 16%"},
            EntrySegment{EntryIcon::NetworkDown, L"999.9 MB/s"},
            EntrySegment{EntryIcon::NetworkUp, L"131.2 KB/s"},
            EntrySegment{EntryIcon::Gpu, L"GPU 8%"},
        };
        if (MeasureEntrySegmentsWidthLogical(changingRates, 96) !=
            MeasureEntrySegmentsWidthLogical(reversedRates, 96))
        {
            failure = L"digit flicker changed the segmented strip width";
            return false;
        }

        // Growth reflows immediately: a reading that genuinely no longer fits its column
        // widens it rather than losing its unit to the ellipsis. This is the failure the
        // fixed slot used to ship - "CPU 4.48 GHz" rendered as "CPU 4.48...".
        if (MeasureEntrySegmentsWidthLogical(
                {
                    EntrySegment{EntryIcon::Cpu, L"CPU 24%"},
                    EntrySegment{EntryIcon::Cpu, L"CPU 4.48 GHz"},
                },
                96) <=
            MeasureEntrySegmentsWidthLogical(
                {
                    EntrySegment{EntryIcon::Cpu, L"CPU 24%"},
                    EntrySegment{EntryIcon::Cpu, L"CPU 26%"},
                },
                96))
        {
            failure = L"a wider reading did not widen its own column";
            return false;
        }

        EntryRenderRequest changingRequest{};
        changingRequest.windowSize = SIZE{kWidth, kHeight};
        changingRequest.monitorCapsule = RECT{0, 0, kWidth, kHeight};
        changingRequest.hasMonitorCapsule = true;
        changingRequest.dpi = 96;
        changingRequest.segments = changingRates;
        EntryRenderRequest reversedRequest = changingRequest;
        reversedRequest.segments = reversedRates;
        const std::vector<SegmentLayout> changingLayout =
            ResolveSegmentLayout(changingRequest);
        const std::vector<SegmentLayout> reversedLayout =
            ResolveSegmentLayout(reversedRequest);
        if (changingLayout.size() != reversedLayout.size())
        {
            failure = L"live readings changed the number of visible segment slots";
            return false;
        }
        for (size_t index = 0; index < changingLayout.size(); ++index)
        {
            const SegmentLayout& before = changingLayout[index];
            const SegmentLayout& after = reversedLayout[index];
            if (before.iconRect.left != after.iconRect.left ||
                before.textRect.left != after.textRect.left ||
                before.textRect.right != after.textRect.right ||
                before.dividerCenterX != after.dividerCenterX)
            {
                failure = L"live readings moved a fixed segment slot";
                return false;
            }
        }

        // A single segment has no neighbour, so it must draw no divider at all. Comparing the
        // two renders is what proves the hairline comes from the separation and not from the
        // segment itself.
        std::vector<BYTE> single;
        if (!compose({EntrySegment{EntryIcon::Cpu, L"CPU 24%"}}, single))
        {
            failure = L"the single-segment strip failed to compose";
            return false;
        }

        const auto inkColumns = [&](const std::vector<BYTE>& pixels)
        {
            int count = 0;
            for (int x = 0; x < kWidth; ++x)
            {
                for (int y = 0; y < kHeight; ++y)
                {
                    if (alphaAt(pixels, x, y) > 96)
                    {
                        ++count;
                        break;
                    }
                }
            }

            return count;
        };

        if (inkColumns(strip) <= inkColumns(single))
        {
            failure = L"three segments did not occupy more of the strip than one";
            return false;
        }

        // More segments than fit must end the strip cleanly rather than clip one in half.
        // Eight readings at the detailed shape is the widest the contract allows, and it does
        // not fit this capsule even folded into four columns.
        std::vector<EntrySegment> tooMany;
        for (int index = 0; index < 8; ++index)
        {
            tooMany.push_back(
                EntrySegment{EntryIcon::Gpu, L"GPU 100% 12.0 GB / 24.0 GB"});
        }

        std::vector<BYTE> overflowing;
        if (!compose(tooMany, overflowing))
        {
            failure = L"the overflowing strip failed to compose";
            return false;
        }

        const int trailing = ScaleLogical(kEntryTrailingPaddingLogical, 96);
        for (int x = kWidth - trailing; x < kWidth; ++x)
        {
            for (int y = 0; y < kHeight; ++y)
            {
                // The capsule fill reaches the edge; only content is forbidden there, and
                // content is what is opaque.
                if (alphaAt(overflowing, x, y) > 200)
                {
                    failure = L"a dropped segment still painted into the trailing padding";
                    return false;
                }
            }
        }

        if (MeasureEntrySegmentsWidthLogical(three, 96) <=
            MeasureEntrySegmentsWidthLogical(
                {EntrySegment{EntryIcon::Cpu, L"CPU 24%"}},
                96))
        {
            failure = L"the segmented measurement does not grow with the segments";
            return false;
        }

        // The history has to reach the bitmap. Comparing the same readings with and without a
        // series is what separates "the graph drew" from "the text happened to be tall".
        std::vector<EntrySegment> plotted = three;
        for (EntrySegment& segment : plotted)
        {
            segment.history.assign(32, 1.0);
        }

        std::vector<BYTE> graphed;
        if (!compose(plotted, graphed))
        {
            failure = L"the plotted strip failed to compose";
            return false;
        }

        const auto inkWeight = [&](const std::vector<BYTE>& pixels)
        {
            long long total = 0;
            for (int y = 0; y < kHeight; ++y)
            {
                for (int x = 0; x < kWidth; ++x)
                {
                    total += alphaAt(pixels, x, y);
                }
            }

            return total;
        };

        if (inkWeight(graphed) <= inkWeight(strip))
        {
            failure = L"the history series left no mark behind its reading";
            return false;
        }

        // An empty series must draw nothing at all, so an unreadable metric is visibly
        // different from one sitting at zero.
        std::vector<EntrySegment> unplotted = three;
        std::vector<BYTE> bare;
        if (!compose(unplotted, bare) || inkWeight(bare) != inkWeight(strip))
        {
            failure = L"an absent history series still painted something";
            return false;
        }

        if (ParseEntryIcon("cpu") != EntryIcon::Cpu ||
            ParseEntryIcon("net-up") != EntryIcon::NetworkUp ||
            ParseEntryIcon("net-down") != EntryIcon::NetworkDown ||
            ParseEntryIcon("rain") != EntryIcon::Rain)
        {
            failure = L"the shared icon token table is wrong";
            return false;
        }

        // A hardware glyph must never inherit the weather illustration.
        if (HasBackdrop(EntryIcon::Cpu) ||
            HasBackdrop(EntryIcon::NetworkUp) ||
            !HasBackdrop(EntryIcon::Rain))
        {
            failure = L"the illustration is claimed for a glyph that has no sky";
            return false;
        }
    }

    if (!IsPointInCapsule(RECT{0, 0, 200, 40}, POINT{100, 20}))
    {
        failure = L"the capsule centre is not hit-testable";
        return false;
    }

    if (IsPointInCapsule(RECT{0, 0, 200, 40}, POINT{0, 0}))
    {
        failure = L"the capsule corner is hit-testable";
        return false;
    }

    EntryVisualAnimator animator;
    animator.SetTarget(EntryVisualState{1, 0, 0});
    int steps = 0;
    while (animator.Advance(1.0 / 60.0, false) && steps < 600)
    {
        ++steps;
    }

    if (steps == 0 || steps >= 600)
    {
        failure = L"the hover animation did not settle within ten seconds";
        return false;
    }

    if (std::abs(animator.Value().hover - 1.0) > kSettleValueEpsilon)
    {
        failure = L"the hover animation settled away from its target";
        return false;
    }

    EntryVisualAnimator reduced;
    reduced.SetTarget(EntryVisualState{0, 0, 1});
    int reducedSteps = 0;
    while (reduced.Advance(1.0 / 60.0, true) && reducedSteps < 600)
    {
        ++reducedSteps;
    }

    if (reducedSteps == 0 || reducedSteps >= steps)
    {
        failure = L"reduced motion is not shorter than the spring";
        return false;
    }

    return true;
}
}
