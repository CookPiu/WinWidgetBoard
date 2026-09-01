using WinWidgetBoard.WorkspacePanel.Layout;
using WinWidgetBoard.WorkspacePanel.Runtime;
using WinWidgetBoard.WorkspacePanel.Settings;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class AddCardViewModelTests
{
    [TestMethod(DisplayName = "UT-GRID-052 [LYT-007] The picker offers every card; a placed type becomes a new instance")]
    public void PickerOffersEveryCardAndDuplicatesPlacedTypes()
    {
        string[] placed =
        [
            BuiltInCardCatalog.NotesInstanceId,
            BuiltInCardCatalog.WeatherInstanceId,
        ];

        var viewModel = new AddCardViewModel(placed);

        Assert.IsTrue(viewModel.HasOptions);
        Assert.AreEqual(BuiltInCardCatalog.Addable.Count, viewModel.Options.Count);
        // A type already on the board is offered again with a fresh identity, so two weather
        // cards can watch two places; a free type keeps its base ID.
        CollectionAssert.Contains(
            viewModel.Options.Select(option => option.InstanceId).ToArray(),
            BuiltInCardCatalog.WeatherInstanceId + "#2");
        CollectionAssert.Contains(
            viewModel.Options.Select(option => option.InstanceId).ToArray(),
            BuiltInCardCatalog.SystemMonitorInstanceId);

        // Neither the unknown fallback nor the retired placeholders are addable on purpose.
        string[] offered = viewModel.Options
            .Select(option => option.InstanceId)
            .ToArray();
        CollectionAssert.DoesNotContain(offered, "demo.unknown");
        CollectionAssert.DoesNotContain(offered, BuiltInCardCatalog.TimerInstanceId);
        CollectionAssert.DoesNotContain(offered, BuiltInCardCatalog.TodoInstanceId);
        CollectionAssert.DoesNotContain(offered, BuiltInCardCatalog.CalendarInstanceId);
    }

    [TestMethod(DisplayName = "UT-GRID-053 [LYT-007] A fully populated board still offers every card as a duplicate")]
    public void FullyPopulatedBoardOffersDuplicates()
    {
        var viewModel = new AddCardViewModel(
            BuiltInCardCatalog.Addable.Select(candidate => candidate.InstanceId));

        Assert.IsTrue(viewModel.HasOptions);
        Assert.AreEqual(BuiltInCardCatalog.Addable.Count, viewModel.Options.Count);
        foreach (AddCardOption option in viewModel.Options)
        {
            StringAssert.EndsWith(option.InstanceId, "#2");
        }

        Assert.IsFalse(viewModel.CanAdd);
        Assert.AreEqual(0, viewModel.Selected.Count);
    }

    [TestMethod(DisplayName = "UT-GRID-054 [LYT-007] Selection drives CanAdd and keeps list order")]
    public void SelectionDrivesCanAddAndKeepsListOrder()
    {
        var viewModel = new AddCardViewModel([]);
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        Assert.IsFalse(viewModel.CanAdd);

        // Selected in reverse order on purpose: several cards added at once must land in the
        // order the picker listed them, not in the order they were clicked.
        AddCardOption last = viewModel.Options[^1];
        AddCardOption first = viewModel.Options[0];
        last.IsSelected = true;
        first.IsSelected = true;

        Assert.IsTrue(viewModel.CanAdd);
        CollectionAssert.Contains(changed, nameof(AddCardViewModel.CanAdd));
        CollectionAssert.AreEqual(
            new[] { first.InstanceId, last.InstanceId },
            viewModel.Selected.Select(option => option.InstanceId).ToArray());

        last.IsSelected = false;
        first.IsSelected = false;
        Assert.IsFalse(viewModel.CanAdd);
    }

    [TestMethod(DisplayName = "UT-GRID-055 [LYT-007] Each option carries the card default size and a stable automation ID")]
    public void EachOptionCarriesDefaultSizeAndStableAutomationId()
    {
        var viewModel = new AddCardViewModel([]);

        AddCardOption monitor = viewModel.Options.Single(
            option => option.InstanceId == BuiltInCardCatalog.SystemMonitorInstanceId);

        Assert.AreEqual(CardSize.L, monitor.DefaultSize);
        Assert.AreEqual("AddCardInclude_demo.sysmon", monitor.IncludeAutomationId);
        Assert.AreEqual(
            viewModel.Options.Count,
            viewModel.Options
                .Select(option => option.IncludeAutomationId)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [TestMethod(DisplayName = "UT-GRID-056 [LYT-007] A missing title falls back to the card type rather than an empty row")]
    public void MissingTitleFallsBackToCardType()
    {
        var viewModel = new AddCardViewModel([], _ => null);

        AddCardOption notes = viewModel.Options.Single(
            option => option.InstanceId == BuiltInCardCatalog.NotesInstanceId);

        Assert.AreEqual(BuiltInCardCatalog.NotesCardTypeId, notes.DisplayName);
    }
}
