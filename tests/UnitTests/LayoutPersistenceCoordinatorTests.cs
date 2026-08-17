using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.Contracts.Protocol;
using WinWidgetBoard.CoreBroker.Client;
using WinWidgetBoard.WorkspacePanel.Layout;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class LayoutPersistenceCoordinatorTests
{
    [TestMethod(DisplayName = "UT-LAYOUT-040 [LYT-007] load validates persisted items and carries revision forward")]
    public async Task LoadValidatesItemsAndCarriesRevisionForward()
    {
        var client = new FakeLayoutClient
        {
            Persisted = new LayoutDto
            {
                Revision = 7,
                Items =
                [
                    new LayoutItemDto
                    {
                        InstanceId = "demo.notes",
                        Order = 0,
                        SizeId = "l",
                        PreferredColumn = 0,
                        PreferredRow = 0,
                    },
                ],
            },
        };
        var coordinator = new LayoutPersistenceCoordinator(
            client,
            CreateLayout());

        LayoutLoadResult? result = await coordinator.LoadAsync(
            CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual("demo.notes", result.Items[0].InstanceId);
        Assert.AreEqual("primary-default", client.LastLayoutId);
        Assert.AreEqual("primary", client.LastDisplayId);

        LayoutSaveRequest request = coordinator.CreateSaveRequest();
        Assert.AreEqual(7, request.ExpectedRevision);
    }

    [TestMethod(DisplayName = "UT-LAYOUT-041 [LYT-007] save maps card layout and updates revision")]
    public async Task SaveMapsLayoutAndUpdatesRevision()
    {
        var client = new FakeLayoutClient
        {
            SaveResult = new LayoutDto { Revision = 5 },
        };
        var coordinator = new LayoutPersistenceCoordinator(
            client,
            CreateLayout());

        LayoutSaveRequest request = coordinator.CreateSaveRequest();
        Assert.AreEqual(0, request.ExpectedRevision);
        Assert.AreEqual("primary-default", request.LayoutId);
        Assert.AreEqual("primary", request.DisplayId);
        Assert.AreEqual(2, request.Items.Count);

        LayoutItemDto notes = request.Items.Single(
            item => item.InstanceId == "demo.notes");
        Assert.AreEqual("l", notes.SizeId);
        Assert.AreEqual(2, notes.ColumnSpan);
        Assert.AreEqual(2, notes.RowSpan);

        await coordinator.SaveAsync(request, CancellationToken.None);

        Assert.AreSame(request, client.LastSaveRequest);
        Assert.AreEqual(5, coordinator.CreateSaveRequest().ExpectedRevision);
    }

    [TestMethod(DisplayName = "UT-LAYOUT-042 [LYT-007] invalid persisted size is rejected")]
    public async Task InvalidPersistedSizeIsRejected()
    {
        var client = new FakeLayoutClient
        {
            Persisted = new LayoutDto
            {
                Items =
                [
                    new LayoutItemDto
                    {
                        InstanceId = "demo.notes",
                        Order = 0,
                        SizeId = "invalid",
                    },
                ],
            },
        };
        var coordinator = new LayoutPersistenceCoordinator(
            client,
            CreateLayout());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => coordinator.LoadAsync(CancellationToken.None));
    }

    private static CardLayoutViewModel CreateLayout() =>
        new(
            4,
            [
                new CardLayoutItem("demo.notes", CardSize.L),
                new CardLayoutItem("demo.weather", CardSize.M),
            ]);

    private sealed class FakeLayoutClient : ILayoutClient
    {
        public LayoutDto? Persisted { get; init; }

        public LayoutDto SaveResult { get; init; } = new();

        public string? LastLayoutId { get; private set; }

        public string? LastDisplayId { get; private set; }

        public LayoutSaveRequest? LastSaveRequest { get; private set; }

        public Task<LayoutDto?> GetLayoutAsync(
            string layoutId,
            string displayId,
            CancellationToken cancellationToken)
        {
            LastLayoutId = layoutId;
            LastDisplayId = displayId;
            return Task.FromResult(Persisted);
        }

        public Task<LayoutDto> SaveLayoutAsync(
            LayoutSaveRequest request,
            CancellationToken cancellationToken)
        {
            LastSaveRequest = request;
            return Task.FromResult(SaveResult);
        }
    }
}
