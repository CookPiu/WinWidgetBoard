using System.Xml.Linq;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardTemplateDragContractTests
{
    private static readonly string[] CardTemplateKeys = [
        "NotesCardTemplate",
        "WeatherCardTemplate",
        "TimerCardTemplate",
        "TodoCardTemplate",
        "CalendarCardTemplate",
        "UnknownCardTemplate",
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

    [TestMethod(DisplayName = "UT-GRID-039 [LYT-003/NFR-A11Y-002] Every card drag handle has a stable unique automation ID")]
    public void EveryCardDragHandleHasStableUniqueAutomationId()
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
        string[] expectedNames = [
            "NotesCardDragHandle",
            "WeatherCardDragHandle",
            "TimerCardDragHandle",
            "TodoCardDragHandle",
            "CalendarCardDragHandle",
            "UnknownCardDragHandle",
        ];

        string[] actualNames = document
            .Descendants(presentation + "TextBlock")
            .Where(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Uid"),
                    "DragCardHandle",
                    StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute(xaml + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();

        CollectionAssert.AreEquivalent(expectedNames, actualNames);
        Assert.AreEqual(expectedNames.Length, actualNames.Distinct().Count());
    }

    [TestMethod(DisplayName = "UT-CARD-013 [CRD-001] Unknown cards use a non-actionable fallback template")]
    public void UnknownCardsUseNonActionableFallbackTemplate()
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
        XElement selector = document
            .Descendants()
            .Single(element =>
                string.Equals(
                    element.Name.LocalName,
                    "CardSurfaceTemplateSelector",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Key"),
                    "CardTemplateSelector",
                    StringComparison.Ordinal));

        Assert.AreEqual(
            "{StaticResource UnknownCardTemplate}",
            (string?)selector.Attribute("DefaultTemplate"));

        XElement unknownTemplate = document
            .Descendants(presentation + "DataTemplate")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Key"),
                    "UnknownCardTemplate",
                    StringComparison.Ordinal));
        string[] allowedButtonUids =
        [
            "DecreaseCardButton",
            "IncreaseCardButton",
            "RemoveCardButton",
        ];
        string[] actualButtonUids = unknownTemplate
            .Descendants(presentation + "Button")
            .Select(element => (string?)element.Attribute(xaml + "Uid"))
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Cast<string>()
            .ToArray();

        CollectionAssert.AreEquivalent(
            allowedButtonUids,
            actualButtonUids);
    }

    [TestMethod(DisplayName = "UT-GRID-045 [LYT-001/NFR-A11Y-002] Card grid exposes a stable automation anchor")]
    public void CardGridExposesStableAutomationAnchor()
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

        XElement cardGridHost = document
            .Descendants(presentation + "Grid")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "CardGridHost",
                    StringComparison.Ordinal));

        Assert.AreEqual(
            "CardGridHost",
            (string?)cardGridHost.Attribute(xaml + "Uid"));
    }

    [TestMethod(DisplayName = "UT-GRID-047 [LYT-003] Resize commands resolve the current repeater item")]
    public void ResizeCommandsResolveTheCurrentRepeaterItem()
    {
        string code = LoadMainWindowCodeBehind();
        int handlerStart = code.IndexOf(
            "private void ResizeCard",
            StringComparison.Ordinal);
        int handlerEnd = code.IndexOf(
            "private void RemoveCardButton_Click",
            handlerStart,
            StringComparison.Ordinal);

        Assert.IsTrue(handlerStart >= 0);
        Assert.IsTrue(handlerEnd > handlerStart);
        string handler = code[handlerStart..handlerEnd];
        StringAssert.Contains(
            handler,
            "ResolveCurrentCardSurfaceItem(sender)");
        StringAssert.Contains(
            handler,
            "_cardEdit.TryStepCardSize(");
        StringAssert.Contains(handler, "item.InstanceId");
        Assert.IsFalse(
            handler.Contains(
                "element.Tag",
                StringComparison.Ordinal));

        StringAssert.Contains(
            code,
            "CardItemsRepeater.GetElementIndex(element)");
        StringAssert.Contains(
            code,
            "_cardSurface.GetItemAt(index)");
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
            "_cardLayout.ReplaceItems(loaded.Items);",
            StringComparison.Ordinal);
        int bindIndex = code.IndexOf(
            "EnsureCardItemsBound();",
            replaceIndex,
            StringComparison.Ordinal);
        Assert.IsTrue(replaceIndex >= 0);
        Assert.IsTrue(bindIndex > replaceIndex);
    }

    [TestMethod(DisplayName = "UT-NOTE-037 [NTE-001/NFR-A11Y-001] Note actions use progressive disclosure inside the fixed card height")]
    public void NoteActionsUseProgressiveDisclosureInsideFixedCardHeight()
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
        XElement notesTemplate = document
            .Descendants(presentation + "DataTemplate")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Key"),
                    "NotesCardTemplate",
                    StringComparison.Ordinal));
        XElement scrollViewer = notesTemplate
            .Descendants(presentation + "ScrollViewer")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "NotesCardScrollViewer",
                    StringComparison.Ordinal));

        Assert.AreEqual(
            "Enabled",
            (string?)scrollViewer.Attribute("VerticalScrollMode"));
        Assert.AreEqual(
            "Disabled",
            (string?)scrollViewer.Attribute("HorizontalScrollMode"));

        XElement dragSurface = notesTemplate
            .Descendants(presentation + "Grid")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "DemoNotesCardSurface",
                    StringComparison.Ordinal));
        foreach (string actionName in new[]
                 {
                     "NewNoteButton",
                     "PreviewNoteButton",
                     "EditMarkdownButton",
                     "NoteMoreButton",
                 })
        {
            XElement action = notesTemplate
                .Descendants()
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute(xaml + "Name"),
                        actionName,
                        StringComparison.Ordinal));
            Assert.IsTrue(action.Ancestors().Contains(dragSurface));
        }

        XElement overflowPanel = notesTemplate
            .Descendants(presentation + "Border")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "NoteOverflowPanel",
                    StringComparison.Ordinal));
        Assert.AreEqual(
            "Collapsed",
            (string?)overflowPanel.Attribute("Visibility"));
        foreach (string actionName in new[]
                 {
                     "MarkdownModeCheckBox",
                     "DeleteCurrentNoteButton",
                     "CopyNoteButton",
                 })
        {
            Assert.IsNotNull(overflowPanel
                .Descendants()
                .SingleOrDefault(element =>
                    string.Equals(
                        (string?)element.Attribute(xaml + "Name"),
                        actionName,
                        StringComparison.Ordinal)));
        }

        XElement footer = notesTemplate
            .Descendants(presentation + "Grid")
            .Single(element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "NoteEditorFooter",
                    StringComparison.Ordinal));
        foreach (string actionName in new[]
                 {
                     "RetryNoteSaveButton",
                     "UndoNoteButton",
                     "RedoNoteButton",
                 })
        {
            Assert.IsNotNull(footer
                .Descendants()
                .SingleOrDefault(element =>
                    string.Equals(
                        (string?)element.Attribute(xaml + "Name"),
                        actionName,
                        StringComparison.Ordinal)));
        }

        Assert.IsTrue(overflowPanel.Ancestors().Contains(scrollViewer));
        Assert.IsTrue(footer.Ancestors().Contains(scrollViewer));
        Assert.IsFalse(dragSurface.Ancestors().Contains(scrollViewer));
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
