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

RECT ResolveTextRect(const EntryRenderRequest& request)
{
    if (request.compact)
    {
        return request.capsule;
    }

    RECT textRect = request.capsule;
    textRect.left += ScaleLogical(kEntryLeadingPaddingLogical, request.dpi) +
        ScaleLogical(kEntryIndicatorWidthLogical, request.dpi) +
        ScaleLogical(kEntryIndicatorGapLogical, request.dpi);
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
    return ComposeText(canvas, request, textColor, textAlpha);
}
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
    const bool compact)
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
        kEntryLeadingPaddingLogical +
        kEntryIndicatorWidthLogical +
        kEntryIndicatorGapLogical +
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
