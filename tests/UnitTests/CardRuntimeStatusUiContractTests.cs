using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using WinWidgetBoard.WorkspacePanel.Runtime;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardRuntimeStatusUiContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod(DisplayName = "UT-CARD-066 [CRD-001/002] Runtime status presentation exposes safe localized state")]
    public void RuntimeStatusPresentationExposesSafeLocalizedState()
    {
        CardRuntimeSnapshot snapshot = CreateSnapshot(
            CardRuntimeStatus.Error,
            CardRuntimeFreshness.Stale,
            payload: new { value = 42 },
            actions: [
                CardRuntimeActionIds.Retry,
                CardRuntimeActionIds.OpenDiagnostics,
                CardRuntimeActionIds.Disable,
            ],
            errorCode: "provider.secret-path");

        CardRuntimeStatusPresentation presentation =
            CardRuntimeStatusPresentation.Create(
                snapshot,
                key => $"localized:{key}",
                timestamp => timestamp.ToString(
                    "O",
                    CultureInfo.InvariantCulture));

        Assert.AreEqual(CardRuntimeStatus.Error, presentation.Status);
        Assert.AreEqual(CardRuntimeFreshness.Stale, presentation.Freshness);
        Assert.IsTrue(presentation.IsStateVisible);
        Assert.IsTrue(presentation.IsContentVisible);
        Assert.IsTrue(presentation.IsCritical);
        Assert.IsTrue(presentation.CanRetry);
        Assert.IsTrue(presentation.CanOpenDiagnostics);
        Assert.IsTrue(presentation.CanDisable);
        Assert.IsFalse(
            presentation.Summary.Contains(
                "provider.secret-path",
                StringComparison.Ordinal));
        Assert.IsFalse(
            presentation.AutomationSummary.Contains(
                "provider.secret-path",
                StringComparison.Ordinal));
    }

    [TestMethod(DisplayName = "UT-CARD-067 [CRD-001/NFR-A11Y-002] Every card template exposes a unique runtime status anchor")]
    public void EveryCardTemplateExposesUniqueRuntimeStatusAnchor()
    {
        XDocument document = LoadAsset("MainWindow.xaml");
        string[] expected = [
            "NotesCardRuntimeStatus",
            "WeatherCardRuntimeStatus",
            "TimerCardRuntimeStatus",
            "TodoCardRuntimeStatus",
            "CalendarCardRuntimeStatus",
            "UnknownCardRuntimeStatus",
        ];

        string[] actual = document
            .Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name) &&
                name.EndsWith("CardRuntimeStatus", StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();

        CollectionAssert.AreEquivalent(expected, actual);
        Assert.AreEqual(expected.Length, actual.Distinct().Count());
    }

    [TestMethod(DisplayName = "UT-CARD-068 [CRD-001/005] Runtime state gates business content and placeholder actions")]
    public void RuntimeStateGatesBusinessContentAndPlaceholderActions()
    {
        XDocument document = LoadAsset("MainWindow.xaml");
        foreach (string templateKey in new[] {
            "NotesCardTemplate",
            "WeatherCardTemplate",
            "TimerCardTemplate",
            "TodoCardTemplate",
            "CalendarCardTemplate",
            "UnknownCardTemplate",
        })
        {
            XElement template = GetTemplate(document, templateKey);
            XElement statusHost = template
                .Descendants()
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute(Xaml + "Name"),
                        templateKey.Replace(
                            "CardTemplate",
                            "CardRuntimeStatus",
                            StringComparison.Ordinal),
                        StringComparison.Ordinal));

            StringAssert.Contains(
                statusHost.ToString(SaveOptions.DisableFormatting),
                "RuntimePresentation");
            StringAssert.Contains(
                statusHost.ToString(SaveOptions.DisableFormatting),
                "AutomationProperties.Name");
            Assert.AreEqual(
                "{x:Bind RuntimePresentation.IsStateVisible, Mode=OneWay}",
                (string?)statusHost.Attribute("Visibility"));
        }

        foreach (string templateKey in new[] {
            "TimerCardTemplate",
            "TodoCardTemplate",
            "CalendarCardTemplate",
        })
        {
            XElement template = GetTemplate(document, templateKey);
            Assert.IsFalse(
                template
                    .Descendants(Presentation + "Button")
                    .Any(element =>
                        string.Equals(
                            (string?)element.Attribute("Click"),
                            "ShellCommandButton_Click",
                            StringComparison.Ordinal)),
                $"{templateKey} must not expose an unimplemented business action.");
        }
    }

    [TestMethod(DisplayName = "UT-CARD-069 [CRD-001/NFR-A11Y-003] Loading uses a static skeleton without decorative loaders")]
    public void LoadingUsesStaticSkeletonWithoutDecorativeLoaders()
    {
        string xaml = File.ReadAllText(GetAssetPath("MainWindow.xaml"));
        StringAssert.Contains(xaml, "CardRuntimeLoadingSkeleton");
        Assert.IsFalse(xaml.Contains("ProgressRing", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Storyboard", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("shimmer", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod(DisplayName = "UT-CARD-070 [NFR-A11Y-003/NFR-I18N-002] Status styles and resources remain themed and symmetric")]
    public void StatusStylesAndResourcesRemainThemedAndSymmetric()
    {
        string styles = File.ReadAllText(GetAssetPath(
            "Styles",
            "WorkspaceVisualStyles.xaml"));
        Assert.IsFalse(styles.Contains('#', StringComparison.Ordinal));
        StringAssert.Contains(styles, "CardRuntimeStatus");

        string mainWindowCode = File.ReadAllText(GetSourcePath(
            "src",
            "WorkspacePanel",
            "MainWindow.xaml.cs"));
        StringAssert.Contains(mainWindowCode, "key.Replace('.', '/')");

        HashSet<string> zhKeys = ReadResourceKeys(
            GetSourcePath("src", "WorkspacePanel", "Strings", "zh-CN", "Resources.resw"));
        HashSet<string> enKeys = ReadResourceKeys(
            GetSourcePath("src", "WorkspacePanel", "Strings", "en-US", "Resources.resw"));

        string[] requiredKeys = [
            "CardStatus.Unknown.Title",
            "CardStatus.Unknown.Summary",
            "CardStatus.Loading.Title",
            "CardStatus.Loading.Summary",
            "CardStatus.Ready.Title",
            "CardStatus.Ready.Summary",
            "CardStatus.Empty.Title",
            "CardStatus.Empty.Summary",
            "CardStatus.Stale.Title",
            "CardStatus.Stale.Summary",
            "CardStatus.Offline.Title",
            "CardStatus.Offline.Summary",
            "CardStatus.PermissionRequired.Title",
            "CardStatus.PermissionRequired.Summary",
            "CardStatus.Unavailable.Title",
            "CardStatus.Unavailable.Summary",
            "CardStatus.Error.Title",
            "CardStatus.Error.Summary",
            "CardStatus.Disabled.Title",
            "CardStatus.Disabled.Summary",
            "CardStatus.Freshness.Stale",
            "CardStatus.Freshness.Offline",
        ];

        foreach (string key in requiredKeys)
        {
            Assert.IsTrue(zhKeys.Contains(key), $"Missing zh-CN resource: {key}");
            Assert.IsTrue(enKeys.Contains(key), $"Missing en-US resource: {key}");
        }

        CollectionAssert.AreEquivalent(zhKeys.ToArray(), enKeys.ToArray());
    }

    private static CardRuntimeSnapshot CreateSnapshot(
        CardRuntimeStatus status,
        CardRuntimeFreshness freshness,
        object payload,
        IEnumerable<string> actions,
        string? errorCode = null) =>
        new(
            "test.status-ui",
            "test.status-card",
            schemaVersion: 1,
            sequence: 1,
            timestampUtc: new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            freshness,
            status,
            JsonSerializer.SerializeToElement(payload),
            actions,
            errorCode);

    private static XElement GetTemplate(XDocument document, string templateKey) =>
        document
            .Descendants(Presentation + "DataTemplate")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(Xaml + "Key"),
                    templateKey,
                    StringComparison.Ordinal));

    private static XDocument LoadAsset(params string[] segments) =>
        XDocument.Load(GetAssetPath(segments));

    private static HashSet<string> ReadResourceKeys(string path) =>
        XDocument.Load(path)
            .Descendants("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static string GetAssetPath(params string[] segments) =>
        Path.Combine([AppContext.BaseDirectory, "TestAssets", .. segments]);

    private static string GetSourcePath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Source asset was not found: {Path.Combine(segments)}");
    }
}
