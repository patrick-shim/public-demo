using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Lges.Ama.AgentHost;

namespace Lges.Ama.AgentHost.Tests;

[TestClass]
public sealed class ToolContentProtectionTests
{
    [TestMethod]
    [TestCategory("Purview")]
    public async Task ToolArgumentProtectionBlocksBeforeInvocation()
    {
        var invocationCount = 0;
        var evaluator = new RecordingEvaluator(ToolContentDirection.Arguments);
        var userContext = new ToolUserContext();
        var protector = CreateProtector(evaluator, userContext);
        var context = CreateInvocationContext(() =>
        {
            invocationCount++;
            return "result";
        });

        using var scope = userContext.Push(Guid.NewGuid());
        await Assert.ThrowsExactlyAsync<ToolContentBlockedException>(async () =>
            await protector.InvokeAsync(context, TestContext.CancellationToken));

        Assert.AreEqual(0, invocationCount);
        CollectionAssert.AreEqual(
            new[] { ToolContentDirection.Arguments },
            evaluator.Directions);
    }

    [TestMethod]
    [TestCategory("Purview")]
    public async Task ToolResultProtectionBlocksAfterOneInvocation()
    {
        var invocationCount = 0;
        var evaluator = new RecordingEvaluator(ToolContentDirection.Result);
        var userContext = new ToolUserContext();
        var protector = CreateProtector(evaluator, userContext);
        var context = CreateInvocationContext(() =>
        {
            invocationCount++;
            return "sensitive result";
        });

        using var scope = userContext.Push(Guid.NewGuid());
        await Assert.ThrowsExactlyAsync<ToolContentBlockedException>(async () =>
            await protector.InvokeAsync(context, TestContext.CancellationToken));

        Assert.AreEqual(1, invocationCount);
        CollectionAssert.AreEqual(
            new[] { ToolContentDirection.Arguments, ToolContentDirection.Result },
            evaluator.Directions);
    }

    [TestMethod]
    public async Task ProtectedToolRequiresHumanUserIdentity()
    {
        var invocationCount = 0;
        var protector = CreateProtector(new RecordingEvaluator(), new ToolUserContext());
        var context = CreateInvocationContext(() =>
        {
            invocationCount++;
            return "result";
        });

        await Assert.ThrowsExactlyAsync<ToolContentEvaluationException>(async () =>
            await protector.InvokeAsync(context, TestContext.CancellationToken));

        Assert.AreEqual(0, invocationCount);
    }

    public TestContext TestContext { get; set; } = null!;

    private static ToolContentProtector CreateProtector(
        IToolContentEvaluator evaluator,
        ToolUserContext userContext) =>
        new(
            evaluator,
            userContext,
            Options.Create(new InternalMcpOptions { MaximumContentCharacters = 8_192 }));

    private static FunctionInvocationContext CreateInvocationContext(Func<string> function)
    {
        var aiFunction = AIFunctionFactory.Create(
            function,
            "test_tool",
            "A test tool");
        return new FunctionInvocationContext
        {
            Function = aiFunction,
            Arguments = new AIFunctionArguments
            {
                ["input"] = "value"
            }
        };
    }

    private sealed class RecordingEvaluator(
        ToolContentDirection? blockedDirection = null) : IToolContentEvaluator
    {
        public List<ToolContentDirection> Directions { get; } = [];

        public ValueTask EvaluateAsync(
            string content,
            Guid userId,
            ToolContentDirection direction,
            CancellationToken cancellationToken)
        {
            Directions.Add(direction);
            if (direction == blockedDirection)
            {
                throw new ToolContentBlockedException(direction);
            }

            return ValueTask.CompletedTask;
        }
    }
}
