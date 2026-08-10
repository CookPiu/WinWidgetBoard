using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWidgetBoard.Contracts;

namespace WinWidgetBoard.UnitTests;

[TestClass]
public sealed class BuildFoundationSmokeTests
{
    [TestMethod(DisplayName = "UT-BUILD-001 [BLD-001] Contracts assembly is loadable")]
    public void ContractsAssemblyIsLoadable()
    {
        var assemblyName = typeof(AssemblyMarker).Assembly.GetName().Name;

        Assert.AreEqual("WinWidgetBoard.Contracts", assemblyName);
    }
}
