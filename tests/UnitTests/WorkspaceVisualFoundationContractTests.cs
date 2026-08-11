using System.Globalization;
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

        AssertMinimumHeight(document, "WwbToolbarButtonStyle", 40);
        AssertMinimumHeight(document, "WwbPrimaryToolbarButtonStyle", 40);
        AssertMinimumHeight(document, "WwbCardActionButtonStyle", 36);
        AssertMinimumHeight(document, "WwbSearchResultButtonStyle", 44);
        AssertMinimumHeight(document, "WwbSearchBoxStyle", 40);

        StringAssert.Contains(source, "{ThemeResource ");
        Assert.IsFalse(
            source.Contains('#', StringComparison.Ordinal),
            "The visual foundation must not bypass system themes with literal colors.");
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
        Assert.AreEqual("1", (string?)commandBar.Attribute("Grid.Row"));

        XElement searchBox = GetNamedElement(document, "TextBox", "SearchBox");
        Assert.AreEqual(
            "{StaticResource WwbSearchBoxStyle}",
            (string?)searchBox.Attribute("Style"));

        XElement searchResults = GetNamedElement(
            document,
            "Border",
            "NoteSearchResultsBorder");
        Assert.AreEqual("2", (string?)searchResults.Attribute("Grid.Row"));
        Assert.AreEqual(
            "{StaticResource WwbSecondarySurfaceStyle}",
            (string?)searchResults.Attribute("Style"));

        XElement status = GetNamedElement(document, "TextBlock", "StatusText");
        Assert.AreEqual(
            "{StaticResource WwbStatusSurfaceStyle}",
            (string?)status.Parent?.Attribute("Style"));
    }

    private static void AssertMinimumHeight(
        XDocument document,
        string styleKey,
        double expectedMinimum)
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
                    "MinHeight",
                    StringComparison.Ordinal));
        double actual = double.Parse(
            (string?)setter.Attribute("Value")
                ?? throw new InvalidDataException(
                    $"{styleKey} has no MinHeight value."),
            CultureInfo.InvariantCulture);

        Assert.IsGreaterThanOrEqualTo(expectedMinimum, actual);
    }

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
