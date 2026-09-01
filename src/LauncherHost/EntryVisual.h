#pragma once

#include <windows.h>

#include <string>
#include <string_view>
#include <vector>

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
// Two steps up from the 14 DIP the strip started at. The capsule is 40 DIP tall inside a
// 48 DIP taskbar, so the extra type costs no height, and it is what pulls the clock and the
// temperature out of "small system text". The entry measures its own content, so the capsule
// widens to match rather than clipping.
inline constexpr int kEntryFontSizeLogical = 16;
inline constexpr int kEntryIconSizeLogical = 18;
inline constexpr int kEntryIconGapLogical = 8;
// Trailing room reserved for the condition illustration, so the motif bleeds off the closing
// cap instead of sitting under the temperature. It mirrors the card, where the illustration
// owns the empty bottom-right corner rather than the text column.
inline constexpr int kEntryMotifWidthLogical = 26;
// Hardware readings are packed two rows deep, the way TrafficMonitor stacks its upload over
// its download. At the entry's own type size a single row runs out of width after four
// readings and still leaves most of the capsule's height empty; a smaller face folded into two
// rows fits the whole configured set and reads as one dense instrument instead of a sentence.
// Segments fill column by column - segment 0 above segment 1, segment 2 above segment 3 - so a
// related pair such as up/down stays together in one column.
inline constexpr int kEntrySegmentRowsPerColumn = 2;
// Still the smaller of the two sizes, but no longer the 10 DIP that made the readings the
// least legible thing on the strip. 11 DIP inside the 15 DIP row keeps both rows clear of
// each other.
inline constexpr int kEntrySegmentFontSizeLogical = 11;
// One text row plus its leading. Two of these stack around the capsule's centre line.
inline constexpr int kEntrySegmentRowHeightLogical = 15;
// The glyph shrinks with the type it labels, and its gap with it; at 18 DIP it would be
// taller than the two rows it sits beside.
inline constexpr int kEntrySegmentIconSizeLogical = 12;
inline constexpr int kEntrySegmentIconGapLogical = 4;
// A column is sized to the wider of its two readings, then quantized and floored so the
// instrument's grid stays put while digits flicker: within one magnitude a reading keeps the
// same character count, and the slack plus the quantum absorb the width of a digit. Only a
// real change of magnitude ("999 KB/s" becoming "1.0 MB/s") re-lays the strip - which is the
// spec's rule that growth must reflow immediately while jitter must not. The floor is the
// old fixed slot, so short readings keep exactly the look they had; the cap keeps one
// detailed reading from spending the whole capsule, with the ellipsis as the last resort.
inline constexpr int kEntrySegmentColumnWidthLogical = 72;
inline constexpr int kEntrySegmentColumnSlackLogical = 6;
inline constexpr int kEntrySegmentColumnQuantumLogical = 12;
inline constexpr int kEntrySegmentColumnMaxWidthLogical = 168;
// Each side of the divider. Tighter than the single-row strip's: with columns half as wide,
// the old 10 DIP read as a hole rather than as breathing room.
inline constexpr int kEntrySegmentColumnGapLogical = 6;
// The divider is a hairline rather than a gap alone: at four columns, spacing by itself stops
// reading as separation. It spans both rows.
inline constexpr int kEntryDividerWidthLogical = 1;
inline constexpr int kEntryDividerHeightLogical = 26;
// Each reading is drawn on top of its own recent history. The graph is a wash rather than a
// line: at this size a stroked plot competes with the digits in front of it, while a filled
// area reads as a shape even when it is barely there.
inline constexpr double kEntrySparklineAlpha = 0.32;
// Inset from the row so neighbouring rows' graphs do not touch.
inline constexpr int kEntrySparklineInsetLogical = 1;

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
    // Hardware monitor glyphs. They share the enum and the drawing primitives with the
    // weather set so both look like they came from the same hand, but they carry no
    // illustration: a condition has a sky, a CPU does not.
    Cpu,
    Memory,
    Gpu,
    Disk,
    NetworkUp,
    NetworkDown,
    Fan,
    Unknown,
};

// Maps a WeatherConditionContract or SystemMonitorContract icon token to a glyph. The two
// token sets are disjoint, so one table serves both. Anything unrecognised becomes Unknown,
// which draws a neutral mark rather than nothing at all.
EntryIcon ParseEntryIcon(std::string_view iconId);

// One piece of a multi-reading entry: a glyph, the text the broker already composed, and the
// recent history it is plotted against.
struct EntrySegment
{
    EntryIcon icon{EntryIcon::None};
    std::wstring text;
    // Oldest first, already normalised to 0..1 by the broker - the entry has no idea what a
    // byte or a percent is, and no way to pick a scale. Empty draws no graph at all, which is
    // how a reading this machine cannot take is distinguished from one that is simply idle.
    std::vector<double> history;
};

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
    // The hardware monitor's own capsule, to the right of the main one with a gap between.
    // It is a second capsule rather than a second region of the first because it is a
    // separate instrument: the main capsule carries the indicator and the panel's state, and
    // mixing a clock and a bank of readings behind one outline read as one crowded control.
    RECT monitorCapsule{};
    bool hasMonitorCapsule{};
    // Drawn inside `monitorCapsule`, never inside `capsule`. Empty leaves that capsule
    // undrawn entirely rather than showing an empty shell.
    std::vector<EntrySegment> segments;
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

// Logical width a segmented entry needs, dividers and per-segment glyphs included.
int MeasureEntrySegmentsWidthLogical(
    const std::vector<EntrySegment>& segments,
    UINT dpi);

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
