namespace Lges.Ama.AgentHost;

internal enum AgentInputRejection
{
    None,
    Empty,
    TooLong
}

internal static class AgentInputGuard
{
    public static AgentInputRejection Evaluate(string? text, int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        if (string.IsNullOrWhiteSpace(text))
        {
            return AgentInputRejection.Empty;
        }

        return text.Trim().Length > maximumCharacters
            ? AgentInputRejection.TooLong
            : AgentInputRejection.None;
    }
}
