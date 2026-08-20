#pragma once

#include <windows.h>

#include <string>
#include <string_view>

namespace winwidgetboard::launcher
{
// Logical metrics for the embedded strip entry. They restate the panel's 4 DIP rhythm for a
// surface that has no XAML and therefore no access to the WwbSpace* tokens.
inline constexpr int kEntryLeadingPaddingLogical = 12;
inline constexpr int kEntryTrailingPaddingLogical = 12;
inline constexpr int kEntryIndicatorWidthLogical = 4;
inline constexpr int kEntryIndicatorGapLogical = 8;
inline constexpr int kEntryIndicatorRestHeightLogical = 4;
inline constexpr int kEntryIndicatorActiveHeightLogical = 16;
inline constexpr int kEntryFontSizeLogical = 14;
inline constexpr int kEntryIconSizeLogical = 18;
inline constexpr int kEntryIconGapLogical = 8;

// The condition glyphs the entry can draw. They are drawn from primitives rather than taken
// from an icon font: the entry composites its own premultiplied bitmap, and a font would add
// an availability dependency for a set this small.
enum class EntryIcon : unsigned char
{
    None,
    ClearDay,
    ClearNight,
    PartlyCloudyDay,
    PartlyCloudyNight,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Thunderstorm,
    Unknown,
};

// Maps a WeatherConditionContract token to a glyph. Anything unrecognised becomes Unknown,
// which draws a neutral mark rather than nothing at all.
EntryIcon ParseEntryIcon(std::string_view conditionIconId);

// Everything the entry needs to paint itself in the user's current theme. The entry draws on
// a layered window, where XAML's ThemeResource lookups do not exist, so the panel's palette
// is mapped down to the smallest honest set: the accent comes from the system highlight
// colour, the neutral tint is plain white or black at a documented opacity chosen by the
// system theme the taskbar itself follows, and high contrast abandons translucency entirely
// and uses nothing but GetSysColor.
struct EntryTheme
{
    bool highContrast{};
    bool darkStrip{};
    COLORREF accent{};
    COLORREF text{};
    COLORREF surfaceTint{};
    COLORREF highContrastFace{};
    COLORREF highContrastActiveFace{};
    COLORREF highContrastText{};
    COLORREF highContrastActiveText{};
    COLORREF highContrastBorder{};
    double restAlpha{};
    double hoverAlpha{};
    double pressedAlpha{};
    double activeAlpha{};
    double textAlpha{};
    double activeTextAlpha{};
};

EntryTheme QueryEntryTheme();

// SPI_GETCLIENTAREAANIMATION is the system's "reduce motion" switch.
bool IsReducedMotionPreferred();

// Continuous interaction state. Every channel is a 0..1 value so a state change can be
// animated rather than snapped.
struct EntryVisualState
{
    double hover{};
    double pressed{};
    double active{};
};

// One critically damped channel per visual property, at the same angular frequency as the
// panel's own open/close spring, so the entry lighting up and the panel expanding settle
// together instead of at two visibly different speeds.
class EntryVisualAnimator final
{
public:
    void SetTarget(const EntryVisualState& target) noexcept;

    // Advances by `seconds` and returns true while any channel is still moving.
    bool Advance(double seconds, bool reducedMotion) noexcept;

    void SnapToTarget() noexcept;

    [[nodiscard]] bool IsSettled() const noexcept;

    [[nodiscard]] const EntryVisualState& Value() const noexcept
    {
        return _value;
    }

private:
    EntryVisualState _value{};
    EntryVisualState _target{};
    EntryVisualState _velocity{};
};

struct EntryRenderRequest
{
    SIZE windowSize{};
    // Client-space rectangle the capsule occupies. In embedded mode this is the whole
    // window; the floating badge keeps inert padding around it.
    RECT capsule{};
    UINT dpi{96};
    // The floating badge is a circle with a centred glyph and no leading indicator.
    bool compact{};
    // Drawn between the indicator and the text. None removes it and its spacing entirely.
    EntryIcon icon{EntryIcon::None};
    std::wstring text;
};

// Composes the entry into a premultiplied BGRA bitmap and hands it to the layered window in
// one UpdateLayeredWindow call, so the capsule edges, the indicator and the text all carry
// real per-pixel alpha instead of a one-bit colour key.
bool RenderEntry(
    HWND window,
    const EntryRenderRequest& request,
    const EntryTheme& theme,
    const EntryVisualState& state,
    std::wstring& error);

// Logical width the entry needs for `text`, before clamping and quantisation.
int MeasureEntryContentWidthLogical(
    const std::wstring& text,
    UINT dpi,
    bool compact,
    EntryIcon icon = EntryIcon::None);

// True when `clientPoint` is inside the capsule itself rather than its bounding box, so the
// rounded corners stay click-through now that no window region clips them.
bool IsPointInCapsule(const RECT& capsule, POINT clientPoint) noexcept;

// Renders synthetic states off-screen and asserts the composition invariants. It does not
// touch the desktop or any real window.
bool RunEntryVisualSmokeTest(std::wstring& failure);

// Diagnostic: writes a contact sheet of every entry state and condition glyph to a BMP, so
// the drawn artwork can be reviewed without a desktop session or a live weather reading.
// Never called by the product; see --entry-icon-preview.
bool WriteEntryPreviewSheet(const std::wstring& path, std::wstring& failure);
}
