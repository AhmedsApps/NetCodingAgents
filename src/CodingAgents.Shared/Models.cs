using System;
using System.Collections.Generic;

namespace CodingAgents.Shared;

public class ConversationSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<PersistedMessage> Messages { get; set; } = new();
}

public class PersistedMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Role { get; set; } = string.Empty; // "User", "Assistant", "Tool", "System"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class TeamWorkflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OriginalTask { get; set; } = string.Empty;
    public string AnalystPlan { get; set; } = string.Empty;
    public string EngineerPlan { get; set; } = string.Empty;
    public string TargetTool { get; set; } = "Antigravity"; // "Antigravity" or "ClaudeCode"
    public string Status { get; set; } = "Pending"; // "Pending", "Queued", "Executing", "Completed", "Failed", "Cancelled"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExecutedAt { get; set; }
    // Optional user-chosen working folder; blank means the worker uses its default per-task
    // folder. Once running, the worker reports back the actual resolved path here.
    public string WorkspacePath { get; set; } = string.Empty;
    public List<WorkflowLog> Logs { get; set; } = new();
}

public class WorkflowLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkflowId { get; set; }
    public string Stage { get; set; } = string.Empty; // "Analyst", "Engineer", "Queue", "Executor", "System", "Error"
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class SystemSettings
{
    public int Id { get; set; } // Single row settings
    public string DefaultExecutor { get; set; } = "Antigravity"; // "Antigravity" or "ClaudeCode"
    public bool EnableWhatsApp { get; set; } = true;
    public bool EnableEmail { get; set; } = false;
    
    // Iterative Review Loop Settings
    public int MaxReviewIterations { get; set; } = 3;

    // Agent Model Routing
    public string AnalystModel { get; set; } = "Ollama:llama3.2:latest";
    public string EngineerModel { get; set; } = "Ollama:llama3.2:latest";
    public string ExecutorModel { get; set; } = "Ollama:llama3.2:latest";
    public string DotNetReviewerModel { get; set; } = "Ollama:llama3.2:latest";
    public string ArchitectReviewerModel { get; set; } = "Ollama:llama3.2:latest";
    public string ChatModel { get; set; } = "Ollama:llama3.2:latest";

    // API Keys and Custom Base URLs (Legacy, moving to ModelConfigurations)
    public string OpenAIApiKey { get; set; } = string.Empty;
    public string OpenAIBaseUrl { get; set; } = string.Empty;
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string AnthropicBaseUrl { get; set; } = string.Empty;
}

// Single-row app access credential. The hash/salt never leave the server.
public class AppCredential
{
    public int Id { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
}

public class ModelConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "Ollama"; // "Ollama", "OpenAI", "Anthropic"
    public string ModelName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

// DTOs for SignalR communication
public class ChatMessageDto
{
    public Guid SessionId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class AgentProgressDto
{
    public Guid SessionId { get; set; }
    public string Type { get; set; } = string.Empty; // "Thought", "ToolCall", "ToolOutput", "CommandOutput", "Status", "Error"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class WorkflowDto
{
    public Guid Id { get; set; }
    public string OriginalTask { get; set; } = string.Empty;
    public string AnalystPlan { get; set; } = string.Empty;
    public string EngineerPlan { get; set; } = string.Empty;
    public string TargetTool { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public string WorkspacePath { get; set; } = string.Empty;
}

public class WorkflowLogDto
{
    public Guid WorkflowId { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
