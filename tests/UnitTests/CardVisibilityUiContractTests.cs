using System.Xml.Linq;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class CardVisibilityUiContractTests
{
    [TestMethod(DisplayName = "UT-CARD-024 [CRD-003] ItemsRepeater forwards realization and clearing signals")]
    public void ItemsRepeaterForwardsRealizationAndClearingSignals()
    {
        XDocument document = XDocument.Load(GetAssetPath("MainWindow.xaml"));
        XElement repeater = document
            .Descendants()
            .Single(element =>
                string.Equals(element.Name.LocalName, "ItemsRepeater", StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name"),
                    "CardItemsRepeater",
                    StringComparison.Ordinal));

        Assert.AreEqual(
            "CardItemsRepeater_ElementPrepared",
            (string?)repeater.Attribute("ElementPrepared"));
        Assert.AreEqual(
            "CardItemsRepeater_ElementClearing",
            (string?)repeater.Attribute("ElementClearing"));

        string source = File.ReadAllText(GetAssetPath("MainWindow.xaml.cs"));
        StringAssert.Contains(source, "CardItemsRepeater_ElementPrepared");
        StringAssert.Contains(source, "CardItemsRepeater_ElementClearing");
        StringAssert.Contains(source, "_realizedCardRuntimes.TryGetValue");
        StringAssert.Contains(source, "_realizedCardRuntimes.Remove");
        StringAssert.Contains(source, "SetViewportVisibility(item.Runtime, true)");
        StringAssert.Contains(source, "SetViewportVisibility(runtime, false)");
    }

    [TestMethod(DisplayName = "UT-CARD-025 [CRD-003] Rebind clears viewport identity before ItemsSource reset")]
    public void RebindClearsViewportIdentityBeforeItemsSourceReset()
    {
        string source = File.ReadAllText(GetAssetPath("MainWindow.xaml.cs"));
        int clearIndex = source.IndexOf(
            "_cardSurface.ClearViewportVisibility();",
            StringComparison.Ordinal);
        int identityClearIndex = source.IndexOf(
            "_realizedCardRuntimes.Clear();",
            StringComparison.Ordinal);
        int resetIndex = source.IndexOf(
            "CardItemsRepeater.ItemsSource = null;",
            StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, clearIndex);
        Assert.IsGreaterThanOrEqualTo(0, identityClearIndex);
        Assert.IsGreaterThan(identityClearIndex, clearIndex);
        Assert.IsGreaterThan(clearIndex, resetIndex);
        StringAssert.Contains(
            source,
            "CardItemsRepeater.ItemsSource = _cardSurface.Items;");
    }

    [TestMethod(DisplayName = "UT-CARD-026 [CRD-003/PNL-004] Panel motion forwards visibility state")]
    public void PanelMotionForwardsVisibilityState()
    {
        string source = File.ReadAllText(GetAssetPath("MainWindow.xaml.cs"));
        int openMethod = source.IndexOf(
            "private void RequestOpenMotion()",
            StringComparison.Ordinal);
        int closeMethod = source.IndexOf(
            "private void RequestCloseMotion()",
            StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, openMethod);
        Assert.IsGreaterThanOrEqualTo(0, closeMethod);

        int openVisibility = source.IndexOf(
            "_cardSurface.SetPanelVisibility(true);",
            openMethod,
            StringComparison.Ordinal);
        int closeVisibility = source.IndexOf(
            "_cardSurface.SetPanelVisibility(false);",
            closeMethod,
            StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(openMethod, openVisibility);
        Assert.IsGreaterThanOrEqualTo(closeMethod, closeVisibility);
    }

    private static string GetAssetPath(params string[] segments) =>
        Path.Combine([AppContext.BaseDirectory, "TestAssets", .. segments]);
}
