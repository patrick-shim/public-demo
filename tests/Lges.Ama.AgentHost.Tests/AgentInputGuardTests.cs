using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentInputGuardTests
{
    [TestMethod]
    public void RejectsEmptyInput()
    {
        Assert.AreEqual(
            "Empty",
            AgentInputGuard.Evaluate("   ", 10).ToString());
    }

    [TestMethod]
    public void RejectsInputAboveCharacterLimit()
    {
        Assert.AreEqual(
            "TooLong",
            AgentInputGuard.Evaluate("123456", 5).ToString());
    }

    [TestMethod]
    public void AcceptsInputAtCharacterLimit()
    {
        Assert.AreEqual(
            "None",
            AgentInputGuard.Evaluate("12345", 5).ToString());
    }
}
