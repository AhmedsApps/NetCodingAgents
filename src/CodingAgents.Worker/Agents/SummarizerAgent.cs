namespace CodingAgents.Worker.Agents;

/// <summary>
/// Condenses the part of a conversation that has aged out of the context window into a
/// rolling summary, so older detail is retained rather than simply dropped.
/// </summary>
public static class SummarizerAgent
{
    public const string Name = "Summarizer";

    public const string Instructions =
        "You maintain a running summary of a developer conversation. " +
        "Merge the previous summary with the new messages into a single concise summary. " +
        "Keep decisions, file names, and unresolved issues. Output only the summary.";
}
