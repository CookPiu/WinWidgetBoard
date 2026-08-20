#include "EntryVisual.h"

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

HFONT CreateEntryFont(const int pixelHeight)
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
        // Grey-scale antialiasing only. ClearType's subpixel coverage has no meaning on a
        // layered window composited over an unknown background and shows colour fringes.
        ANTIALIASED_QUALITY,
        DEFAULT_PITCH | FF_SWISS,
        L"Segoe UI");
}

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

RECT ResolveTextRect(const EntryRenderRequest& request)
{
    if (request.compact)
    {
        return request.capsule;
    }

    RECT textRect = request.capsule;
    textRect.left += ScaleLogical(LeadingWidthLogical(request.icon), request.dpi);
    textRect.right -= ScaleLogical(kEntryTrailingPaddingLogical, request.dpi);
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

    HFONT font = CreateEntryFont(ResolveFontPixelHeight(request));
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
            const BYTE coverage =
                std::max(source[0], std::max(source[1], source[2]));
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

void ComposeCapsule(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const EntryTheme& theme,
    const EntryVisualState& state)
{
    const double scale = 1.0 - (1.0 - kPressScale) * Clamp01(state.pressed);
    const double centerX =
        (static_cast<double>(request.capsule.left) +
            static_cast<double>(request.capsule.right)) / 2.0;
    const double centerY =
        (static_cast<double>(request.capsule.top) +
            static_cast<double>(request.capsule.bottom)) / 2.0;
    const double halfWidth = RectWidthOf(request.capsule) / 2.0 * scale;
    const double halfHeight = RectHeightOf(request.capsule) / 2.0 * scale;
    const double radius = std::min(halfWidth, halfHeight);

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
                centerX,
                centerY,
                halfWidth,
                halfHeight,
                radius);
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

void AddCloud(IconMask& mask, const double offsetY)
{
    AddUnitCircle(mask, 0.34, 0.54 + offsetY, 0.17);
    AddUnitCircle(mask, 0.52, 0.46 + offsetY, 0.22);
    AddUnitCircle(mask, 0.71, 0.56 + offsetY, 0.15);
    AddUnitRoundedRect(mask, 0.52, 0.63 + offsetY, 0.58, 0.20, 0.10);
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

void ComposeIcon(
    Canvas& canvas,
    const EntryRenderRequest& request,
    const COLORREF color,
    const double alpha)
{
    if (request.compact || request.icon == EntryIcon::None || alpha <= 0.0)
    {
        return;
    }

    const RECT box = ResolveIconRect(request);
    const int size = box.right - box.left;
    if (size <= 0)
    {
        return;
    }

    IconMask mask{};
    mask.width = canvas.width;
    mask.height = canvas.height;
    mask.coverage.assign(
        static_cast<size_t>(mask.width) * static_cast<size_t>(mask.height),
        0.0);
    mask.unit = static_cast<double>(size);
    mask.originX = static_cast<double>(box.left);
    mask.originY = static_cast<double>(box.top);
    BuildIconMask(mask, request.icon);

    for (int y = 0; y < canvas.height; ++y)
    {
        for (int x = 0; x < canvas.width; ++x)
        {
            const double coverage = mask.coverage[
                static_cast<size_t>(y) * static_cast<size_t>(canvas.width) +
                    static_cast<size_t>(x)];
            if (coverage > 0.0)
            {
                canvas.Blend(x, y, color, coverage * alpha);
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

    ComposeCapsule(canvas, request, theme, state);
    ComposeIndicator(canvas, request, theme, state);

    const COLORREF textColor = theme.highContrast
        ? MixColor(
            theme.highContrastText,
            theme.highContrastActiveText,
            Clamp01(state.active))
        : theme.text;
    const double textAlpha = theme.highContrast
        ? 1.0
        : Lerp(theme.textAlpha, theme.activeTextAlpha, Clamp01(state.active));
    ComposeIcon(canvas, request, textColor, textAlpha);
    return ComposeText(canvas, request, textColor, textAlpha);
}
}

EntryIcon ParseEntryIcon(const std::string_view conditionIconId)
{
    // The tokens are WeatherConditionContract values; keep them in one place on this side
    // too so a new condition is a single edit rather than a scattered one.
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

    HFONT font = CreateEntryFont(ScaleLogical(kEntryFontSizeLogical, dpi));
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
        kEntryTrailingPaddingLogical;
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
    const int sheetWidth = gap + (tileWidth + gap) * 2;
    const int sheetHeight = gap + (tileHeight + gap) * kIconCount;

    const COLORREF darkStrip = RGB(32, 34, 38);
    const COLORREF lightStrip = RGB(214, 224, 222);

    std::vector<BYTE> sheet(
        static_cast<size_t>(sheetWidth) * static_cast<size_t>(sheetHeight) * 3,
        0);
    for (int y = 0; y < sheetHeight; ++y)
    {
        for (int x = 0; x < sheetWidth; ++x)
        {
            const bool rightColumn = x > sheetWidth / 2;
            const COLORREF background = rightColumn ? lightStrip : darkStrip;
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
                column == 0 ? darkStrip : lightStrip);
        }
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

        // The glyph has to claim its own width, otherwise it would overlap the text.
        const int withoutIcon =
            MeasureEntryContentWidthLogical(L"26", 96, false, EntryIcon::None);
        const int withIcon =
            MeasureEntryContentWidthLogical(L"26", 96, false, EntryIcon::Rain);
        if (withIcon - withoutIcon !=
            kEntryIconSizeLogical + kEntryIconGapLogical)
        {
            failure = L"the condition glyph does not reserve its own width";
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
