using System.Text.Json;
using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Notes;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

/// <summary>
/// A weather card is between one and eight grid cells, and what fits differs at every one of
/// them. The card used to show the same stack at every size: at the short sizes the trend was
/// clipped mid-row, and at the tall ones the space under the reading stayed empty because the
/// forecast was gated on the largest size alone.
///
/// The disclosure is computed here rather than measured in XAML, so the rule is stated once
/// and testable without a desktop.
/// </summary>
[TestClass]
public sealed class WeatherCardDisclosureTests
{
    private const string Payload = """
        {
          "location": { "label": "Beijing" },
          "current": {
            "temperatureC": 26.4,
            "apparentTemperatureC": 32.4,
            "relativeHumidityPercent": 87,
            "windSpeedKmh": 1.6,
            "weatherCode": 96,
            "conditionIconId": "thunderstorm",
            "todayHighTemperatureC": 28.9,
            "todayLowTemperatureC": 16.6,
            "observedAtLocal": "2026-08-25T14:15"
          },
          "hourly": [
            { "timeLocal": "2026-08-25T15:00", "temperatureC": 25.9, "conditionIconId": "rain" },
            { "timeLocal": "2026-08-25T16:00", "temperatureC": 25.4, "conditionIconId": "rain" }
          ],
          "daily": [
            { "dateLocal": "2026-08-26", "highTemperatureC": 24.6, "lowTemperatureC": 19.2, "conditionIconId": "rain" },
            { "dateLocal": "2026-08-27", "highTemperatureC": 29.4, "lowTemperatureC": 20.1, "conditionIconId": "clear-day" }
          ]
        }
        """;

    [TestMethod(DisplayName =
        "UT-WEA-024 [WEA-001] Each card size discloses only what its cells can hold")]
    public async Task EachSizeDisclosesOnlyWhatItsCellsHold()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateWeatherItem(noteEditor, CardSize.S);
        ApplyPayload(item);

        // One cell: the place, the number, the sky and today's range - about 80 DIP of the 98
        // a one-row card has. The named readings cost 34 more and do not fit.
        Assert.IsTrue(item.HasWeatherHighLow);
        Assert.IsFalse(item.HasWeatherSecondary);
        Assert.IsFalse(item.HasWeatherHours);
        Assert.IsFalse(item.HasWeatherDays);
        Assert.IsFalse(item.HasWeatherFooter);
        // Too narrow to name the two ends; the values carry a slash between them instead.
        Assert.IsFalse(item.IsWeatherHighLowLabelVisible);
        Assert.IsTrue(item.IsWeatherHighLowSeparatorVisible);

        // Two cells across, one down. Still one row of height, so the named readings still do
        // not fit - the width only buys room to name the range.
        item.UpdatePlacement(Place(CardSize.M));
        Assert.IsFalse(item.HasWeatherSecondary);
        Assert.IsTrue(item.IsWeatherHighLowLabelVisible);
        Assert.IsFalse(item.IsWeatherHighLowSeparatorVisible);
        Assert.IsFalse(item.HasWeatherHours);
        Assert.IsFalse(item.HasWeatherDays);

        // Four across but still one down. The trend fits only because it goes beside the
        // reading rather than under it; the days would need a second row and do not.
        item.UpdatePlacement(Place(CardSize.W));
        Assert.IsTrue(item.IsWeatherWideLayout);
        Assert.IsFalse(item.HasWeatherSecondary);
        Assert.IsTrue(item.HasWeatherHours);
        Assert.IsFalse(item.HasWeatherDays);

        // Two down: the forecast stacks under the reading, which is what earns the days.
        // This is the size the card ships at, and it showed no days at all before.
        item.UpdatePlacement(Place(CardSize.L));
        Assert.IsFalse(item.IsWeatherWideLayout);
        Assert.IsTrue(item.HasWeatherSecondary);
        Assert.IsTrue(item.HasWeatherHours);
        Assert.IsTrue(item.HasWeatherDays);
        Assert.IsTrue(item.HasWeatherFooter);

        item.UpdatePlacement(Place(CardSize.XL));
        Assert.IsTrue(item.IsWeatherWideLayout);
        Assert.IsTrue(item.HasWeatherHours);
        Assert.IsTrue(item.HasWeatherDays);
    }

    [TestMethod(DisplayName =
        "UT-WEA-029 [WEA-001] Shrinking and growing again brings the forecast back")]
    public async Task ShrinkingAndGrowingAgainBringsTheForecastBack()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateWeatherItem(noteEditor, CardSize.L);
        ApplyPayload(item);
        Assert.IsTrue(item.HasWeatherHours);
        Assert.IsTrue(item.HasWeatherDays);

        // A resize is not a refresh: the projection is untouched, so what a size took away a
        // size has to give back. If this only came back on the next snapshot, a card resized
        // between two provider ticks would sit there without its forecast for a quarter hour.
        item.UpdatePlacement(Place(CardSize.M));
        Assert.IsFalse(item.HasWeatherHours);
        Assert.IsFalse(item.HasWeatherDays);

        item.UpdatePlacement(Place(CardSize.L));
        Assert.IsTrue(item.HasWeatherHours);
        Assert.IsTrue(item.HasWeatherDays);
    }

    [TestMethod(DisplayName =
        "UT-WEA-025 [WEA-001] The forecast changes cells, not templates, when the card is wide")]
    public async Task ForecastMovesCellsRatherThanTemplates()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateWeatherItem(noteEditor, CardSize.L);
        ApplyPayload(item);

        // Stacked: the forecast is the row under the reading, and the reading takes the
        // whole width. Without the span the reading would be squeezed into half a card by
        // a column that has nothing in it.
        Assert.AreEqual(0, item.WeatherForecastColumn);
        Assert.AreEqual(1, item.WeatherForecastRow);
        Assert.AreEqual(2, item.WeatherCurrentColumnSpan);
        // The forecast spans too. Left in the Auto column it is measured against infinity,
        // and the day rows - whose high sits in a star column - arrange past the card's
        // clip: the weekday and its glyph showed, the two temperatures did not.
        Assert.AreEqual(2, item.WeatherForecastColumnSpan);

        item.UpdatePlacement(Place(CardSize.XL));

        Assert.AreEqual(1, item.WeatherForecastColumn);
        Assert.AreEqual(0, item.WeatherForecastRow);
        Assert.AreEqual(1, item.WeatherCurrentColumnSpan);
        Assert.AreEqual(1, item.WeatherForecastColumnSpan);
    }

    [TestMethod(DisplayName =
        "UT-WEA-026 [WEA-001] Resizing republishes every size-gated property")]
    public async Task ResizingRepublishesEverySizeGatedProperty()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        using CardSurfaceItem item = CreateWeatherItem(noteEditor, CardSize.S);
        ApplyPayload(item);

        var changed = new List<string>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);
        item.UpdatePlacement(Place(CardSize.XL));

        // Resizing raises Placement only. A gated section that is not republished here keeps
        // whatever visibility it had when the snapshot last arrived, so a card grown to fit
        // the forecast sits there without one until the next refresh happens to land.
        foreach (string property in new[]
                 {
                     nameof(CardSurfaceItem.HasWeatherHours),
                     nameof(CardSurfaceItem.HasWeatherDays),
                     nameof(CardSurfaceItem.HasWeatherSecondary),
                     nameof(CardSurfaceItem.HasWeatherFooter),
                     nameof(CardSurfaceItem.IsWeatherWideLayout),
                     nameof(CardSurfaceItem.WeatherForecastColumn),
                     nameof(CardSurfaceItem.WeatherForecastRow),
                     nameof(CardSurfaceItem.WeatherCurrentColumnSpan),
                     nameof(CardSurfaceItem.WeatherForecastColumnSpan),
                 })
        {
            CollectionAssert.Contains(changed, property, $"{property} was not republished.");
        }
    }

    [TestMethod(DisplayName =
        "UT-WEA-027 [WEA-001] The condition reads in the panel's language")]
    public async Task ConditionReadsInThePanelsLanguage()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        // The card said "Thunderstorm with hail" in a Chinese panel, because the projection
        // hard-codes the English WMO descriptions. It now names the string and the surface
        // item resolves it through the panel's own resource loader.
        using CardSurfaceItem item = CreateWeatherItem(
            noteEditor,
            CardSize.L,
            key => key == "WeatherConditionThunderstormHail" ? "雷阵雨伴冰雹" : null);
        ApplyPayload(item);

        Assert.AreEqual("雷阵雨伴冰雹", item.WeatherConditionText);
    }

    [TestMethod(DisplayName =
        "UT-WEA-028 [WEA-001] An unnamed condition falls back to the description, not to blank")]
    public async Task AnUnnamedConditionFallsBackToTheDescription()
    {
        await using var noteEditor = new NoteEditorViewModel(null);
        // A resolver that finds nothing is the same case as a WMO code with no localized
        // name: the line has to keep saying something rather than emptying itself.
        using CardSurfaceItem item = CreateWeatherItem(
            noteEditor,
            CardSize.L,
            _ => string.Empty);
        ApplyPayload(item);

        Assert.AreEqual("Thunderstorm with hail", item.WeatherConditionText);
    }

    private static CardSurfaceItem CreateWeatherItem(
        NoteEditorViewModel noteEditor,
        CardSize size,
        Func<string, string?>? resourceResolver = null)
    {
        var layout = new CardLayoutViewModel(
            4,
            [new CardLayoutItem(BuiltInCardCatalog.WeatherInstanceId, size)]);
        return new CardSurfaceItem(
            Place(size),
            noteEditor,
            new NoteSearchViewModel(null),
            new CardLayoutEditViewModel(layout),
            runtimeResourceResolver: resourceResolver ?? (static key => key));
    }

    private static CardPlacement Place(CardSize size)
    {
        CardSpan span = ResponsiveGridLayout.GetSpan(size);
        return new CardPlacement(
            BuiltInCardCatalog.WeatherInstanceId,
            size,
            0,
            0,
            span.Columns,
            span.Rows);
    }

    private static void ApplyPayload(CardSurfaceItem item)
    {
        using JsonDocument document = JsonDocument.Parse(Payload);
        Assert.IsTrue(
            item.Runtime.ApplyRemoteSnapshot(
                new CardRuntimeSnapshot(
                    BuiltInCardCatalog.WeatherInstanceId,
                    BuiltInCardCatalog.WeatherCardTypeId,
                    BuiltInCardRuntimeFactory.CurrentSchemaVersion,
                    sequence: 1,
                    new DateTimeOffset(2026, 8, 25, 6, 15, 0, TimeSpan.Zero),
                    CardRuntimeFreshness.Fresh,
                    CardRuntimeStatus.Ready,
                    document.RootElement.Clone())));
    }
}
