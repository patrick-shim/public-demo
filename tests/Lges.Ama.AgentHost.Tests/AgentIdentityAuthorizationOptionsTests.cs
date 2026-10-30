using System.Text.Json;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Authentication.Msal.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class AgentIdentityAuthorizationOptionsTests
{
    private static readonly string[] ExpectedAgenticHandlers =
        ["agentic-foundry", "agentic-purview", "agentic-mcp"];
    private static readonly string[] ExpectedOboHandlers =
        ["obo-user"];

    [TestMethod]
    public void FrontendModesUseDisjointAutoSignInHandlers()
    {
        var options = new AgentIdentityAuthorizationOptions();
        var agenticHandlers = options.GetHandlers(AgentFrontendMode.AgenticUser)
            .GetAutoSignInHandlerNames(includePurview: true, includeInternalMcp: true);
        var oboHandlers = options.GetHandlers(AgentFrontendMode.OnBehalfOf)
            .GetAutoSignInHandlerNames(includePurview: true, includeInternalMcp: true);

        CollectionAssert.AreEqual(
            ExpectedAgenticHandlers,
            agenticHandlers);
        CollectionAssert.AreEqual(
            ExpectedOboHandlers,
            oboHandlers);
        Assert.IsFalse(agenticHandlers.Intersect(oboHandlers, StringComparer.Ordinal).Any());
    }

    [TestMethod]
    public void OboHandlerReturnsRawBlueprintUserAssertion()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "appsettings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(path));
        var handlers = settings.RootElement
            .GetProperty("AgentApplication")
            .GetProperty("UserAuthorization")
            .GetProperty("Handlers");

        foreach (var handlerName in ExpectedOboHandlers)
        {
            var handler = handlers.GetProperty(handlerName);
            var handlerSettings = handler.GetProperty("Settings");

            Assert.AreEqual(
                "AzureBotUserAuthorization",
                handler.GetProperty("Type").GetString());
            Assert.AreEqual(
                "lges-ama-obo",
                handlerSettings.GetProperty("AzureBotOAuthConnectionName").GetString());
            Assert.IsFalse(handlerSettings.TryGetProperty("OBOConnectionName", out _));
            Assert.IsFalse(handlerSettings.TryGetProperty("OBOScopes", out _));
        }
    }

    [TestMethod]
    public void ChildIdentityConnectionsUseBlueprintFederation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var settings = JsonDocument.Parse(File.ReadAllText(path));
        var connections = settings.RootElement.GetProperty("Connections");

        foreach (var connectionName in new[] { "ServiceConnection", "OboServiceConnection" })
        {
            var connection = connections
                .GetProperty(connectionName)
                .GetProperty("Settings");

            Assert.AreEqual(
                "FederatedCredentials",
                connection.GetProperty("AuthType").GetString());
            Assert.IsTrue(connection.TryGetProperty("ClientId", out _));
            Assert.IsTrue(connection.TryGetProperty("FederatedClientId", out _));
        }

        Assert.IsTrue(connections
            .GetProperty("ServiceConnection")
            .GetProperty("Settings")
            .TryGetProperty("AgentId", out _));
        Assert.IsFalse(connections
            .GetProperty("OboServiceConnection")
            .GetProperty("Settings")
            .TryGetProperty("AgentId", out _));

        var obo = settings.RootElement.GetProperty("AgentIdentityObo");
        Assert.IsTrue(obo.TryGetProperty("AgentId", out _));
        Assert.AreEqual(
            "OboServiceConnection",
            obo.GetProperty("BlueprintConnectionName").GetString());
    }

    [TestMethod]
    public void FederatedBlueprintConnectionCreatesConfidentialClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDefaultMsalAuth(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var settings = new ConnectionSettings
        {
            AuthType = AuthTypes.FederatedCredentials,
            Authority = "https://login.microsoftonline.com/11111111-1111-4111-8111-111111111111",
            ClientId = "22222222-2222-4222-8222-222222222222",
            FederatedClientId = "33333333-3333-4333-8333-333333333333"
        };

        var client = new MsalAuth(provider, settings).CreateClientApplication();

        Assert.IsInstanceOfType<IConfidentialClientApplication>(client);
    }
}
