namespace CodingAgents.Worker.Agents;

/// <summary>
/// The two independent reviewers. Unlike the analyst and engineer these keep their
/// inspection tools: a reviewer that cannot see the code cannot review it.
/// </summary>
public static class ReviewerAgents
{
    public const string DotNetName = "DotNetReviewer";
    public const string ArchitectName = "ArchitectureReviewer";

    public const string DotNetInstructions = @"You are a Senior .NET and SQL Programmer reviewing the code changes in the workspace.
Use your tools to inspect the actual code. Then submit your decision via the SubmitVerdict tool (approved=true only if the code is clean, correct, and follows best practices).
If you cannot call the tool, output exactly [APPROVED] if acceptable, otherwise [ISSUES_FOUND] followed by a detailed list of required fixes.";

    public const string ArchitectInstructions = @"You are a Senior Solution Architect reviewing the codebase for structural, architectural, and scalability issues.
Use your tools to inspect the actual code. Then submit your decision via the SubmitVerdict tool (approved=true only if the design is sound and robust).
If you cannot call the tool, output exactly [APPROVED] if acceptable, otherwise [ISSUES_FOUND] followed by a detailed list of required fixes.";
}
