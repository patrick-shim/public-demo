using Microsoft.Agents.Core.Models;
using Lges.Ama.AgentHost;
using DiagnosticActivity = System.Diagnostics.Activity;
using ModelActivity = Microsoft.Agents.Core.Models.Activity;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentTurnDiagnosticsTests
{
    [TestMethod]
    public void ScopeUsesHashesInsteadOfConversationIdentifiers()
    {
        const string conversationId = "conversation-sensitive-canary";
        const string activityId = "activity-sensitive-canary";
        using var trace = new DiagnosticActivity("turn").Start();
        var activity = new ModelActivity
        {
            Id = activityId,
            Conversation = new ConversationAccount { Id = conversationId }
        };

        var values = AgentTurnDiagnostics.CreateScopeValues(
            AgentFrontendMode.OnBehalfOf,
            activity);
        var rendered = string.Join(";", values.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.AreEqual("OnBehalfOf", values["FrontendMode"]);
        Assert.AreEqual(trace.TraceId.ToString(), values["TraceId"]);
        Assert.AreEqual(24, values["ConversationHash"].ToString()!.Length);
        Assert.AreEqual(24, values["ActivityHash"].ToString()!.Length);
        Assert.DoesNotContain(conversationId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(activityId, rendered, StringComparison.Ordinal);
    }

    [TestMethod]
    public void IdentifierHashesAreStableAndDistinct()
    {
        var first = AgentTurnDiagnostics.HashIdentifier("first");

        Assert.AreEqual(first, AgentTurnDiagnostics.HashIdentifier("first"));
        Assert.AreNotEqual(first, AgentTurnDiagnostics.HashIdentifier("second"));
        Assert.AreEqual(string.Empty, AgentTurnDiagnostics.HashIdentifier(null));
    }
}
