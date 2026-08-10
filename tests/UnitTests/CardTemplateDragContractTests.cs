using System.Xml.Linq;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardTemplateDragContractTests
{
    private static readonly string[] CardTemplateKeys = [
        "NotesCardTemplate",
        "TimerCardTemplate",
        "TodoCardTemplate",
        "CalendarCardTemplate",
    ];

    private static readonly string[] PointerHandlerAttributes = [
        "PointerCaptureLost",
        "PointerCanceled",
        "PointerMoved",
        "PointerPressed",
        "PointerReleased",
    ];

    [TestMethod(DisplayName = "UT-GRID-028 [LYT-003] Every card template routes drag from its root surface")]
    public void EveryCardTemplateRoutesDragFromRootSurface()
    {
        string xamlPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (string templateKey in CardTemplateKeys)
        {
            XElement template = document
                .Descendants(presentation + "DataTemplate")
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute(xaml + "Key"),
                        templateKey,
                        StringComparison.Ordinal));
            XElement rootSurface = template.Elements().Single();

            Assert.AreEqual(presentation + "Border", rootSurface.Name);
            Assert.AreEqual(
                "{x:Bind InstanceId}",
                (string?)rootSurface.Attribute("Tag"));
            foreach (string attributeName in PointerHandlerAttributes)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(
                    (string?)rootSurface.Attribute(attributeName)));
            }
        }
    }

    [TestMethod(DisplayName = "UT-GRID-029 [LYT-004] Unified entry selects the validated x64 panel artifact")]
    public void UnifiedEntrySelectsValidatedX64PanelArtifact()
    {
        string scriptPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "Run-WinWidgetBoard.ps1");
        string script = File.ReadAllText(scriptPath);

        StringAssert.Contains(
            script,
            @"WinWidgetBoard.WorkspacePanel\x64\Release\");
        Assert.IsFalse(
            script.Contains(
                @"WinWidgetBoard.WorkspacePanel\Release\",
                StringComparison.Ordinal));
    }

    [TestMethod(DisplayName = "UT-GRID-033 [LYT-004] Committed placements invalidate the custom grid")]
    public void CommittedPlacementsInvalidateCustomGrid()
    {
        string code = LoadMainWindowCodeBehind();
        int handlerStart = code.IndexOf(
            "private void CardLayout_PropertyChanged",
            StringComparison.Ordinal);
        int handlerEnd = code.IndexOf(
            "private void CardSurface_PropertyChanged",
            handlerStart,
            StringComparison.Ordinal);

        Assert.IsTrue(handlerStart >= 0);
        Assert.IsTrue(handlerEnd > handlerStart);
        string handler = code[handlerStart..handlerEnd];
        StringAssert.Contains(
            handler,
            "e.PropertyName == nameof(CardLayoutViewModel.Placements)");
        StringAssert.Contains(
            handler,
            "_cardGridLayout.InvalidatePlacements();");
    }

    [TestMethod(DisplayName = "UT-GRID-034 [DAT-001] Persisted layout precedes first repeater binding")]
    public void PersistedLayoutPrecedesFirstRepeaterBinding()
    {
        string code = LoadMainWindowCodeBehind();
        int constructorStart = code.IndexOf(
            "public MainWindow(",
            StringComparison.Ordinal);
        int constructorEnd = code.IndexOf(
            "public NoteEditorViewModel NoteEditor",
            constructorStart,
            StringComparison.Ordinal);
        Assert.IsTrue(constructorStart >= 0);
        Assert.IsTrue(constructorEnd > constructorStart);
        string constructor = code[constructorStart..constructorEnd];
        Assert.IsFalse(
            constructor.Contains(
                "CardItemsRepeater.ItemsSource",
                StringComparison.Ordinal));

        int replaceIndex = code.IndexOf(
            "_cardLayout.ReplaceItems(replay.Items);",
            StringComparison.Ordinal);
        int bindIndex = code.IndexOf(
            "EnsureCardItemsBound();",
            replaceIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(replaceIndex >= 0);
        Assert.IsTrue(bindIndex > replaceIndex);
    }

    private static string LoadMainWindowCodeBehind()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "MainWindow.xaml.cs");
        return File.ReadAllText(path);
    }
}
