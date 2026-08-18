using System.Xml.Linq;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class WorkspaceVisualFoundationContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] CardTemplateKeys = [
        "NotesCardTemplate",
        "TimerCardTemplate",
        "TodoCardTemplate",
        "CalendarCardTemplate",
    ];

    [TestMethod(DisplayName = "UT-UI-001 [PNL-006/NFR-A11Y-003] App loads the shared visual foundation")]
    public void AppLoadsSharedVisualFoundation()
    {
        XDocument document = LoadAsset("App.xaml");

        string[] mergedSources = document
            .Descendants(Presentation + "ResourceDictionary")
            .Select(element => (string?)element.Attribute("Source"))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Cast<string>()
            .ToArray();

        CollectionAssert.Contains(
            mergedSources,
            "Styles/WorkspaceVisualStyles.xaml");
    }

    [TestMethod(DisplayName = "UT-UI-002 [CRD-004/NFR-A11Y-005] Shared styles preserve theme and target sizes")]
    public void SharedStylesPreserveThemeAndTargetSizes()
    {
        XDocument document = LoadAsset(
            "Styles",
            "WorkspaceVisualStyles.xaml");
        string source = File.ReadAllText(GetAssetPath(
            "Styles",
            "WorkspaceVisualStyles.xaml"));

        string[] requiredStyleKeys = [
            "WwbPanelRootStyle",
            "WwbHeaderSurfaceStyle",
            "WwbCardSurfaceStyle",
            "WwbSecondarySurfaceStyle",
            "WwbPanelTitleStyle",
            "WwbCardTitleStyle",
            "WwbWeatherTemperatureStyle",
            "WwbToolbarButtonStyle",
            "WwbPrimaryToolbarButtonStyle",
            "WwbCardActionButtonStyle",
            "WwbSearchResultButtonStyle",
            "WwbSearchBoxStyle",
        ];
        string[] actualStyleKeys = document
            .Descendants(Presentation + "Style")
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToArray();

        foreach (string key in requiredStyleKeys)
        {
            CollectionAssert.Contains(actualStyleKeys, key);
        }

        AssertStyleSetterValue(
            document,
            "WwbToolbarButtonStyle",
            "MinHeight",
            "32");
        AssertStyleSetterValue(
            document,
            "WwbPrimaryToolbarButtonStyle",
            "MinHeight",
            "32");
        AssertStyleSetterValue(
            document,
            "WwbCardActionButtonStyle",
            "MinHeight",
            "32");
        AssertStyleSetterValue(
            document,
            "WwbSearchResultButtonStyle",
            "MinHeight",
            "36");
        AssertStyleSetterValue(
            document,
            "WwbSearchBoxStyle",
            "MinHeight",
            "32");
        AssertStyleSetterValue(
            document,
            "WwbPanelTitleStyle",
            "FontSize",
            "16");
        AssertStyleSetterValue(
            document,
            "WwbCardTitleStyle",
            "FontSize",
            "13");
        AssertStyleSetterValue(
            document,
            "WwbWeatherTemperatureStyle",
            "FontSize",
            "28");

        StringAssert.Contains(source, "{ThemeResource ");
        Assert.IsFalse(
            source.Contains('#', StringComparison.Ordinal),
            "The visual foundation must not bypass system themes with literal colors.");

        AssertThemeThickness(document, "Default", "0");
        AssertThemeThickness(document, "HighContrast", "1");
    }

    [TestMethod(DisplayName = "UT-UI-003 [LYT-003/CRD-004] Card surfaces share a shell and hide edit tools by state")]
    public void CardSurfacesShareShellAndHideEditToolsByState()
    {
        XDocument document = LoadAsset("MainWindow.xaml");

        foreach (string templateKey in CardTemplateKeys)
        {
            XElement template = GetDataTemplate(document, templateKey);
            XElement cardSurface = template.Elements().Single();

            Assert.AreEqual(
                "{StaticResource WwbCardSurfaceStyle}",
                (string?)cardSurface.Attribute("Style"));

            XElement editToolbar = template
                .Descendants(Presentation + "Border")
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute("Style"),
                        "{StaticResource WwbEditToolbarSurfaceStyle}",
                        StringComparison.Ordinal));
            Assert.AreEqual(
                "{x:Bind IsEditing, Mode=OneWay}",
                (string?)editToolbar.Attribute("Visibility"));

            string runtimeStatusName = templateKey switch
            {
                "NotesCardTemplate" => "NotesCardRuntimeStatus",
                "TimerCardTemplate" => "TimerCardRuntimeStatus",
                "TodoCardTemplate" => "TodoCardRuntimeStatus",
                "CalendarCardTemplate" => "CalendarCardRuntimeStatus",
                _ => throw new AssertFailedException(
                    $"Unexpected card template: {templateKey}"),
            };
            XElement runtimeStatusHost = template
                .Descendants()
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute(Xaml + "Name"),
                        runtimeStatusName,
                        StringComparison.Ordinal));
            StringAssert.Contains(
                runtimeStatusHost.ToString(SaveOptions.DisableFormatting),
                "RuntimePresentation");

            if (!string.Equals(
                    templateKey,
                    "NotesCardTemplate",
                    StringComparison.Ordinal))
            {
                Assert.IsFalse(template
                    .Descendants(Presentation + "Button")
                    .Any(element =>
                        string.Equals(
                            (string?)element.Attribute("Style"),
                            "{StaticResource WwbCardActionButtonStyle}",
                            StringComparison.Ordinal)),
                    $"{templateKey} must not expose an unimplemented business action.");
                Assert.AreEqual(
                    "3",
                    (string?)editToolbar.Attribute("Grid.Row"));
            }
        }
    }

    [TestMethod(DisplayName = "UT-UI-004 [PNL-006/PNL-007] Header, commands and content expose a calm hierarchy")]
    public void HeaderCommandsAndContentExposeCalmHierarchy()
    {
        XDocument document = LoadAsset("MainWindow.xaml");

        XElement root = GetNamedElement(document, "Grid", "RootGrid");
        Assert.AreEqual(
            "{StaticResource WwbPanelRootStyle}",
            (string?)root.Attribute("Style"));

        XElement header = GetNamedElement(document, "Border", "HeaderBar");
        Assert.AreEqual(
            "{StaticResource WwbHeaderSurfaceStyle}",
            (string?)header.Attribute("Style"));

        XElement commandBar = GetNamedElement(
            document,
            "Grid",
            "HeaderCommandBar");
        Assert.AreEqual("0", (string?)commandBar.Attribute("Grid.Row"));

        XElement searchBox = GetNamedElement(document, "TextBox", "SearchBox");
        Assert.AreEqual(
            "{StaticResource WwbSearchBoxStyle}",
            (string?)searchBox.Attribute("Style"));
        Assert.AreEqual("1", (string?)searchBox.Attribute("Grid.Column"));
        Assert.IsTrue(searchBox.Ancestors().Contains(commandBar));

        foreach (string name in new[]
                 {
                     "GreetingText",
                     "ListNotesButton",
                     "EditLayoutButton",
                     "SettingsButton",
                     "ClosePanelButton",
                 })
        {
            XElement element = document
                .Descendants()
                .Single(candidate =>
                    string.Equals(
                        (string?)candidate.Attribute(Xaml + "Name"),
                        name,
                        StringComparison.Ordinal));
            Assert.IsTrue(
                element.Ancestors().Contains(commandBar),
                $"{name} must share the compact header row.");
        }

        XElement searchResults = GetNamedElement(
            document,
            "Border",
            "NoteSearchResultsBorder");
        Assert.AreEqual("1", (string?)searchResults.Attribute("Grid.Row"));
        Assert.AreEqual(
            "{StaticResource WwbSecondarySurfaceStyle}",
            (string?)searchResults.Attribute("Style"));

        XElement status = GetNamedElement(document, "TextBlock", "StatusText");
        Assert.AreEqual(
            "{StaticResource WwbStatusSurfaceStyle}",
            (string?)status.Parent?.Attribute("Style"));
        Assert.AreEqual(
            header,
            status.Ancestors(Presentation + "Border")
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute(Xaml + "Name"),
                        "HeaderBar",
                        StringComparison.Ordinal)));

        XElement content = GetNamedElement(
            document,
            "ScrollViewer",
            "ContentScrollViewer");
        Assert.AreEqual(
            "Top",
            (string?)content.Attribute("VerticalContentAlignment"));
        Assert.AreEqual(
            "Stretch",
            (string?)content.Attribute("HorizontalContentAlignment"));
        XElement cardGrid = content.Elements().Single();
        Assert.AreEqual(
            "CardGridHost",
            (string?)cardGrid.Attribute(Xaml + "Name"));
        Assert.AreEqual(
            "Top",
            (string?)cardGrid.Attribute("VerticalAlignment"));
    }

    [TestMethod(DisplayName = "UT-UI-005 [PNL-006/NTE-001/WEA-001] Quiet canvas prioritizes live content over chrome")]
    public void QuietCanvasPrioritizesLiveContentOverChrome()
    {
        XDocument document = LoadAsset("MainWindow.xaml");
        string codeBehind = File.ReadAllText(
            GetAssetPath("MainWindow.xaml.cs"));
        XElement notes = GetDataTemplate(document, "NotesCardTemplate");
        XElement notesSurface = notes.Elements().Single();

        Assert.AreEqual("160", (string?)notesSurface.Attribute("MinHeight"));
        Assert.IsFalse(notes
            .Descendants(Presentation + "TextBlock")
            .Any(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Uid"),
                    "NotesCardBody",
                    StringComparison.Ordinal)));

        XElement noteBody = GetNamedElement(document, "TextBox", "NoteBodyBox");
        Assert.AreEqual(
            "112",
            (string?)noteBody.Attribute("Height"));
        Assert.AreEqual(
            "Disabled",
            (string?)noteBody.Attribute(
                "ScrollViewer.HorizontalScrollBarVisibility"));

        foreach (string actionName in new[]
                 {
                     "RetryNoteSaveButton",
                     "UndoNoteButton",
                     "RedoNoteButton",
                 })
        {
            XElement action = GetNamedElement(document, "Button", actionName);
            StringAssert.Contains(
                (string?)action.Attribute("Visibility") ?? string.Empty,
                actionName switch
                {
                    "RetryNoteSaveButton" => "CanRetrySave",
                    "UndoNoteButton" => "CanUndo",
                    "RedoNoteButton" => "CanRedo",
                    _ => throw new AssertFailedException(
                        $"Unexpected note action: {actionName}"),
                });
        }

        XElement weatherTemperature = document
            .Descendants(Presentation + "TextBlock")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(
                        XName.Get(
                            "AutomationProperties.AutomationId",
                            "using:Microsoft.UI.Xaml.Automation")),
                    "WeatherTemperatureText",
                    StringComparison.Ordinal));
        Assert.AreEqual(
            "{StaticResource WwbWeatherTemperatureStyle}",
            (string?)weatherTemperature.Attribute("Style"));

        Assert.IsFalse(document
            .Descendants()
            .Any(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    "AddCardButton",
                    StringComparison.Ordinal)));

        foreach (string locale in new[] { "zh-CN", "en-US" })
        {
            XDocument resources = LoadAsset(
                "Strings",
                locale,
                "Resources.resw");
            Assert.AreEqual(
                "\uE8FD",
                GetResourceValue(resources, "ListNotesButton.Content"));
            Assert.AreEqual(
                "\uE70F",
                GetResourceValue(resources, "EditLayoutButton.Content"));
            Assert.AreEqual(
                "\uE713",
                GetResourceValue(resources, "SettingsButton.Content"));
            Assert.AreEqual(
                "\uE711",
                GetResourceValue(resources, "ClosePanelButton.Content"));
        }

        StringAssert.Contains(
            codeBehind,
            "_resources.GetString(\"EditLayoutButton/Content\")");
        Assert.IsFalse(
            codeBehind.Contains(
                "_resources.GetString(\"EditLayoutButton.Content\")",
                StringComparison.Ordinal),
            "MRT resource paths must use '/' for a XAML resource property.");
    }

    private static void AssertThemeThickness(
        XDocument document,
        string themeKey,
        string expected)
    {
        XElement theme = document
            .Descendants(Presentation + "ResourceDictionary")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Key"),
                    themeKey,
                    StringComparison.Ordinal));
        XElement thickness = theme
            .Elements(Presentation + "Thickness")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Key"),
                    "WwbSurfaceBorderThickness",
                    StringComparison.Ordinal));

        Assert.AreEqual(expected, thickness.Value);
    }

    private static void AssertStyleSetterValue(
        XDocument document,
        string styleKey,
        string property,
        string expected)
    {
        XElement style = document
            .Descendants(Presentation + "Style")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Key"),
                    styleKey,
                    StringComparison.Ordinal));
        XElement setter = style
            .Elements(Presentation + "Setter")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute("Property"),
                    property,
                    StringComparison.Ordinal));
        Assert.AreEqual(expected, (string?)setter.Attribute("Value"));
    }

    private static string GetResourceValue(
        XDocument document,
        string key) =>
        document
            .Descendants("data")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute("name"),
                    key,
                    StringComparison.Ordinal))
            .Element("value")?.Value
            ?? throw new InvalidDataException(
                $"Resource {key} has no value.");

    private static XElement GetDataTemplate(
        XDocument document,
        string templateKey)
    {
        return document
            .Descendants(Presentation + "DataTemplate")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Key"),
                    templateKey,
                    StringComparison.Ordinal));
    }

    private static XElement GetNamedElement(
        XDocument document,
        string elementName,
        string name)
    {
        return document
            .Descendants(Presentation + elementName)
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    name,
                    StringComparison.Ordinal));
    }

    private static XDocument LoadAsset(params string[] segments)
    {
        return XDocument.Load(GetAssetPath(segments));
    }

    private static string GetAssetPath(params string[] segments)
    {
        return Path.Combine(
            [AppContext.BaseDirectory, "TestAssets", .. segments]);
    }
}
