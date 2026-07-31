using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CodingAgents.Shared;
using CodingAgents.Server.Data;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Security.Cryptography;

namespace CodingAgents.Server.Hubs;

public class ChatHub : Hub
{
    private readonly ChatDbContext _dbContext;
    private readonly AppSettings _settings;
    private readonly Services.WorkflowManager _workflowManager;
    private readonly Services.WorkerRegistry _workers;
    private readonly IWebHostEnvironment _env;
    private readonly Services.PasswordService _passwords;
    private readonly Services.TokenStore _tokens;
    private readonly string _workerKey;

    // Serializes creation of the single settings row so concurrent callers can't each
    // insert their own copy when the table is still empty.
    private static readonly SemaphoreSlim _settingsInitLock = new(1, 1);

    public ChatHub(ChatDbContext dbContext, IOptions<AppSettings> settings, Services.WorkflowManager workflowManager, Services.WorkerRegistry workers, IWebHostEnvironment env, Services.PasswordService passwords, Services.TokenStore tokens, IConfiguration configuration)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
        _workflowManager = workflowManager;
        _workers = workers;
        _env = env;
        _passwords = passwords;
        _tokens = tokens;
        _workerKey = configuration["WorkerKey"] ?? "change-me-worker-key";
    }

    // ---- Authentication ------------------------------------------------------------
    // Every other hub method is gated by AuthHubFilter; only the three methods below are
    // callable anonymously.

    /// <summary>Verifies the password and, on success, authenticates this connection and
    /// returns a token the client can use to re-authenticate after a reconnect.</summary>
    public async Task<string?> Login(string password)
    {
        if (!await _passwords.VerifyAsync(_dbContext, password))
        {
            // Slow down brute-force attempts a little.
            await Task.Delay(500);
            return null;
        }

        AuthHubFilter.MarkAuthenticated(Context);
        return _tokens.Issue();
    }

    /// <summary>Re-authenticates a (re)connected client using a token from Login.</summary>
    public async Task<bool> Authenticate(string token)
    {
        if (!_tokens.Validate(token))
        {
            await Task.Delay(500);
            return false;
        }

        AuthHubFilter.MarkAuthenticated(Context);
        return true;
    }

    /// <summary>Returns an error message, or null when the password was changed.</summary>
    public async Task<string?> ChangePassword(string currentPassword, string newPassword)
    {
        var error = await _passwords.ChangeAsync(_dbContext, currentPassword, newPassword);
        if (error == null)
        {
            // Old tokens must not survive a password change.
            _tokens.RevokeAll();
        }
        return error;
    }

    /// <summary>True while the password is still the shipped default.</summary>
    public Task<bool> IsDefaultPassword()
        => _passwords.IsDefaultAsync(_dbContext);

    /// <summary>Issues a short-lived token the client appends to /artifacts URLs so the
    /// browser can load private screenshots and uploaded files.</summary>
    public Task<string> GetArtifactToken()
        => Task.FromResult(_tokens.IssueArtifactToken());

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_workers.Unregister(Context.ConnectionId))
        {
            Console.WriteLine("[ChatHub] Local PC Worker disconnected.");

            // Recover any workflows this worker was running so they don't hang forever.
            var interrupted = _workers.ReleaseWorkflowsForConnection(Context.ConnectionId);
            await _workflowManager.RequeueInterruptedWorkflowsAsync(interrupted);

            // Let clients update their "agent online/offline" indicator live.
            await Clients.All.SendAsync("WorkerStatusChanged", _workers.IsAnyConnected);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinSession(string sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task LeaveSession(string sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId);
    }

    public async Task<List<ConversationSession>> GetSessions()
    {
        return await _dbContext.Sessions
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<ConversationSession> CreateSession(string title)
    {
        var session = new ConversationSession
        {
            Title = string.IsNullOrWhiteSpace(title) ? $"Session {DateTime.Now:yyyy-MM-dd HH:mm}" : title
        };
        _dbContext.Sessions.Add(session);
        await _dbContext.SaveChangesAsync();
        return session;
    }

    public async Task<List<PersistedMessage>> GetMessages(Guid sessionId)
    {
        return await _dbContext.Messages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Timestamp)
            .ToListAsync();
    }

    public async Task SendMessage(Guid sessionId, string content)
    {
        var groupName = sessionId.ToString();

        // 1. Save and broadcast User message
        var userMsg = new PersistedMessage
        {
            SessionId = sessionId,
            Role = "User",
            Content = content,
            Timestamp = DateTime.UtcNow
        };
        _dbContext.Messages.Add(userMsg);
        await _dbContext.SaveChangesAsync();

        await Clients.Group(groupName).SendAsync("ReceiveMessage", new ChatMessageDto
        {
            SessionId = sessionId,
            Role = "User",
            Content = content,
            Timestamp = userMsg.Timestamp
        });

        // 2. Delegate to local PC worker if connected
        try
        {
            var currentConnectionId = _workers.GetWorkerConnectionId();
            if (string.IsNullOrEmpty(currentConnectionId))
            {
                await ReportWorkerProgress(sessionId, "Error", "Error: Local PC Agent Worker is offline. Please start it to send messages.");
                return;
            }

            await Clients.Group(groupName).SendAsync("ReceiveProgress", new AgentProgressDto
            {
                SessionId = sessionId,
                Type = "Info",
                Content = "Relaying prompt to local PC worker agent..."
            });

            await Clients.Client(currentConnectionId).SendAsync("ExecuteChatAgent", sessionId, content);
        }
        catch (Exception ex)
        {
            await ReportWorkerProgress(sessionId, "Error", $"Dispatcher Error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>Registers a local PC agent worker. Requires the shared worker key, since a
    /// rogue "worker" could otherwise receive and execute tasks.</summary>
    public async Task<bool> RegisterWorker(string workerKey)
    {
        var expected = _workerKey;
        if (string.IsNullOrEmpty(expected) || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(workerKey ?? string.Empty),
                System.Text.Encoding.UTF8.GetBytes(expected)))
        {
            Console.WriteLine("[ChatHub] Rejected worker registration: invalid worker key.");
            await Task.Delay(500);
            return false;
        }

        AuthHubFilter.MarkAuthenticated(Context);
        _workers.Register(Context.ConnectionId);
        Console.WriteLine($"[ChatHub] Local PC Worker registered. Connection ID: {Context.ConnectionId}");

        // Notify clients so their "agent online" indicator flips without a refresh.
        await Clients.All.SendAsync("WorkerStatusChanged", _workers.IsAnyConnected);
        return true;
    }

    public Task<bool> IsWorkerConnected()
    {
        return Task.FromResult(_workers.IsAnyConnected);
    }

    public async Task<List<string>> GetLocalModels(string baseUrl)
    {
        var currentConnectionId = _workers.GetWorkerConnectionId();
        if (string.IsNullOrEmpty(currentConnectionId))
        {
            return new List<string>();
        }
        try
        {
            return await Clients.Client(currentConnectionId).InvokeAsync<List<string>>("GetInstalledModels", baseUrl, default);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatHub] Error invoking GetInstalledModels on worker: {ex.Message}");
            return new List<string> { "Error invoking worker: " + ex.Message };
        }
    }

    public async Task<string> GetActiveModel()
    {
        var currentConnectionId = _workers.GetWorkerConnectionId();
        if (string.IsNullOrEmpty(currentConnectionId))
        {
            return "No active worker connected";
        }
        try
        {
            return await Clients.Client(currentConnectionId).InvokeAsync<string>("GetActiveModel", default);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatHub] Error invoking GetActiveModel on worker: {ex.Message}");
            return "Error retrieving model: " + ex.Message;
        }
    }

    // Returns the single settings row, creating it if the table is empty. Creation is
    // serialized and double-checked so concurrent hub invocations don't insert duplicates.
    private async Task<SystemSettings> GetOrCreateSettingsAsync()
    {
        var settings = await _dbContext.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync();
        if (settings != null) return settings;

        await _settingsInitLock.WaitAsync();
        try
        {
            settings = await _dbContext.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync();
            if (settings == null)
            {
                settings = new SystemSettings
                {
                    DefaultExecutor = "Antigravity",
                    EnableWhatsApp = true,
                    EnableEmail = false
                };
                _dbContext.Settings.Add(settings);
                await _dbContext.SaveChangesAsync();
            }
            return settings;
        }
        finally
        {
            _settingsInitLock.Release();
        }
    }

    public async Task SaveActiveModel(string modelName)
    {
        var settings = await GetOrCreateSettingsAsync();
        settings.ChatModel = modelName;
        await _dbContext.SaveChangesAsync();

        var currentConnectionId = _workers.GetWorkerConnectionId();
        if (!string.IsNullOrEmpty(currentConnectionId))
        {
            await Clients.Client(currentConnectionId).SendAsync("SetActiveModel", modelName);
        }
    }

    public async Task ProcessQueue()
    {
        await _workflowManager.ProcessQueueAsync();
    }

    public async Task ReportWorkerProgress(Guid sessionId, string type, string content)
    {
        var progressMsg = new PersistedMessage
        {
            SessionId = sessionId,
            Role = $"Progress:{type}",
            Content = content,
            Timestamp = DateTime.UtcNow
        };
        _dbContext.Messages.Add(progressMsg);
        await _dbContext.SaveChangesAsync();

        await Clients.Group(sessionId.ToString()).SendAsync("ReceiveProgress", new AgentProgressDto
        {
            SessionId = sessionId,
            Type = type,
            Content = content,
            Timestamp = progressMsg.Timestamp
        });
    }

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    // Receives a file the user attached in the chat: stores it under the per-session artifacts
    // folder (so it shows in the conversation) and relays it to the worker so the agent can
    // read it from the session's workspace folder.
    public async Task UploadChatFile(Guid sessionId, string fileName, string base64Data)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64Data); }
        catch
        {
            await ReportWorkerProgress(sessionId, "Error", "Failed to read the uploaded file.");
            return;
        }

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "upload.bin";
        var uniqueName = $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{safeName}";

        var sessionDir = Path.Combine(_env.ContentRootPath, "artifacts", sessionId.ToString());
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllBytesAsync(Path.Combine(sessionDir, uniqueName), bytes);

        var relativeUrl = $"/artifacts/{sessionId}/{uniqueName}";
        bool isImage = ImageExtensions.Contains(Path.GetExtension(safeName), StringComparer.OrdinalIgnoreCase);
        var role = isImage ? "UserImage" : "UserFile";

        var msg = new PersistedMessage
        {
            SessionId = sessionId,
            Role = role,
            Content = relativeUrl,
            Timestamp = DateTime.UtcNow
        };
        _dbContext.Messages.Add(msg);
        await _dbContext.SaveChangesAsync();

        await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", new ChatMessageDto
        {
            SessionId = sessionId,
            Role = role,
            Content = relativeUrl,
            Timestamp = msg.Timestamp
        });

        // Relay to the worker so the file lands in the session's workspace folder and the
        // agent can open it by name.
        var worker = _workers.GetWorkerConnectionId();
        if (!string.IsNullOrEmpty(worker))
        {
            await Clients.Client(worker).SendAsync("ReceiveChatFile", sessionId, safeName, base64Data);
        }
    }

    // Receives an image (e.g. a screenshot) captured by the worker, stores it under a
    // per-session artifacts folder, and posts it into the conversation as an image message.
    public async Task ReportWorkerImage(Guid sessionId, string fileName, string base64Data)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64Data);

            var safeName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "image.png";
            // Timestamp-prefix so repeated captures (all named screenshot.png) don't overwrite.
            var uniqueName = $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{safeName}";

            var sessionDir = Path.Combine(_env.ContentRootPath, "artifacts", sessionId.ToString());
            Directory.CreateDirectory(sessionDir);
            await File.WriteAllBytesAsync(Path.Combine(sessionDir, uniqueName), bytes);

            var relativeUrl = $"/artifacts/{sessionId}/{uniqueName}";

            var imageMsg = new PersistedMessage
            {
                SessionId = sessionId,
                Role = "Image",
                Content = relativeUrl,
                Timestamp = DateTime.UtcNow
            };
            _dbContext.Messages.Add(imageMsg);
            await _dbContext.SaveChangesAsync();

            await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", new ChatMessageDto
            {
                SessionId = sessionId,
                Role = "Image",
                Content = relativeUrl,
                Timestamp = imageMsg.Timestamp
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatHub] Error saving worker image: {ex.Message}");
            await ReportWorkerProgress(sessionId, "Error", $"Failed to attach image: {ex.Message}");
        }
    }

    public async Task ReportWorkerResponse(Guid sessionId, string content)
    {
        var assistantMsg = new PersistedMessage
        {
            SessionId = sessionId,
            Role = "Assistant",
            Content = content,
            Timestamp = DateTime.UtcNow
        };
        _dbContext.Messages.Add(assistantMsg);
        await _dbContext.SaveChangesAsync();

        await Clients.Group(sessionId.ToString()).SendAsync("ReceiveMessage", new ChatMessageDto
        {
            SessionId = sessionId,
            Role = "Assistant",
            Content = content,
            Timestamp = assistantMsg.Timestamp
        });
    }

    public async Task ReportWorkflowUpdate(Guid workflowId, string? analystPlan, string? engineerPlan, string status, string? targetTool)
    {
        var workflow = await _dbContext.Workflows.FindAsync(workflowId);
        if (workflow != null)
        {
            workflow.Status = status;
            if (analystPlan != null) workflow.AnalystPlan = analystPlan;
            if (engineerPlan != null) workflow.EngineerPlan = engineerPlan;
            if (targetTool != null) workflow.TargetTool = targetTool;
            if (status == "Executing" && workflow.ExecutedAt == null) workflow.ExecutedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        // Once a workflow settles or is handed back to the queue, the worker is no longer
        // running it, so drop ownership to avoid a stale disconnect re-queue later.
        if (status is "Completed" or "Failed" or "Stalemate" or "Queued" or "Cancelled")
        {
            _workers.ReleaseWorkflow(workflowId);
        }

        // Push the new state directly so the client (and its pipeline graph) updates in
        // lockstep with the logs, instead of relying on a separate re-fetch round-trip.
        if (workflow != null)
        {
            await BroadcastWorkflowStateAsync(workflow);
        }
        await Clients.All.SendAsync("ReceiveWorkflowUpdate");
    }

    public async Task ReportWorkflowLog(Guid workflowId, string stage, string message)
    {
        var log = new WorkflowLog
        {
            WorkflowId = workflowId,
            Stage = stage,
            Message = message,
            Timestamp = DateTime.UtcNow
        };
        _dbContext.WorkflowLogs.Add(log);
        await _dbContext.SaveChangesAsync();

        await Clients.Group(workflowId.ToString()).SendAsync("ReceiveWorkflowLog", new WorkflowLogDto
        {
            WorkflowId = workflowId,
            Stage = stage,
            Message = message,
            Timestamp = log.Timestamp
        });
        await Clients.All.SendAsync("ReceiveWorkflowUpdate");
    }

    public async Task<SystemSettings> GetSettings()
    {
        return await GetOrCreateSettingsAsync();
    }

    public async Task<SystemSettings> SaveSettings(SystemSettings newSettings)
    {
        var settings = await GetOrCreateSettingsAsync();

        settings.DefaultExecutor = newSettings.DefaultExecutor;
        settings.EnableWhatsApp = newSettings.EnableWhatsApp;
        settings.EnableEmail = newSettings.EnableEmail;
        settings.MaxReviewIterations = newSettings.MaxReviewIterations;
        settings.AnalystModel = newSettings.AnalystModel;
        settings.EngineerModel = newSettings.EngineerModel;
        settings.ExecutorModel = newSettings.ExecutorModel;
        settings.DotNetReviewerModel = newSettings.DotNetReviewerModel;
        settings.ArchitectReviewerModel = newSettings.ArchitectReviewerModel;
        settings.ChatModel = newSettings.ChatModel;
        settings.OpenAIApiKey = newSettings.OpenAIApiKey;
        settings.OpenAIBaseUrl = newSettings.OpenAIBaseUrl;
        settings.AnthropicApiKey = newSettings.AnthropicApiKey;
        settings.AnthropicBaseUrl = newSettings.AnthropicBaseUrl;

        await _dbContext.SaveChangesAsync();
        return settings;
    }

    public async Task<List<WorkflowDto>> GetWorkflows()
    {
        return await _dbContext.Workflows
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new WorkflowDto
            {
                Id = w.Id,
                OriginalTask = w.OriginalTask,
                AnalystPlan = w.AnalystPlan,
                EngineerPlan = w.EngineerPlan,
                TargetTool = w.TargetTool,
                Status = w.Status,
                CreatedAt = w.CreatedAt,
                ExecutedAt = w.ExecutedAt,
                WorkspacePath = w.WorkspacePath
            })
            .ToListAsync();
    }

    public async Task<List<WorkflowLogDto>> GetWorkflowLogs(Guid workflowId)
    {
        return await _dbContext.WorkflowLogs
            .Where(l => l.WorkflowId == workflowId)
            .OrderBy(l => l.Timestamp)
            .Select(l => new WorkflowLogDto
            {
                WorkflowId = l.WorkflowId,
                Stage = l.Stage,
                Message = l.Message,
                Timestamp = l.Timestamp
            })
            .ToListAsync();
    }

    public async Task<WorkflowDto> SubmitWorkflow(string originalTask, string? workspacePath)
    {
        var workflow = new TeamWorkflow
        {
            OriginalTask = originalTask,
            Status = "Pending",
            WorkspacePath = workspacePath?.Trim() ?? string.Empty
        };
        _dbContext.Workflows.Add(workflow);
        await _dbContext.SaveChangesAsync();

        // Start processing asynchronously in background
        _workflowManager.StartWorkflow(workflow.Id);

        return new WorkflowDto
        {
            Id = workflow.Id,
            OriginalTask = workflow.OriginalTask,
            Status = workflow.Status,
            CreatedAt = workflow.CreatedAt,
            WorkspacePath = workflow.WorkspacePath
        };
    }

    // Requests cancellation of a running workflow. The worker cancels the in-flight pipeline
    // and reports back "Cancelled"; if no worker is connected we settle it here directly.
    public async Task CancelWorkflow(Guid workflowId)
    {
        var worker = _workers.GetWorkerConnectionId();
        if (!string.IsNullOrEmpty(worker))
        {
            await Clients.Client(worker).SendAsync("CancelWorkflow", workflowId);
        }

        var wf = await _dbContext.Workflows.FindAsync(workflowId);
        if (wf != null && wf.Status is not ("Completed" or "Failed" or "Stalemate" or "Cancelled"))
        {
            wf.Status = string.IsNullOrEmpty(worker) ? "Cancelled" : "Cancelling";
            await _dbContext.SaveChangesAsync();
        }
        await ReportWorkflowLog(workflowId, "System", "🛑 Cancellation requested by user.");
        await Clients.All.SendAsync("ReceiveWorkflowUpdate");
    }

    // The worker asks whether a long-running agent should keep going. Relayed to whoever is
    // viewing that session/workflow; the answer comes back via SubmitRetryDecision.
    public async Task RequestContinueDecision(Guid id, bool isWorkflow, string message)
    {
        if (isWorkflow)
        {
            var wf = await _dbContext.Workflows.FindAsync(id);
            if (wf != null)
            {
                wf.Status = "AwaitingRetryConfirmation";
                await _dbContext.SaveChangesAsync();
                await BroadcastWorkflowStateAsync(wf);
            }
        }
        await Clients.Group(id.ToString()).SendAsync("ContinuePrompt", id, isWorkflow, message);
        await Clients.All.SendAsync("ReceiveWorkflowUpdate");
    }

    public async Task SubmitRetryDecision(Guid workflowId, bool retry)
    {
        var worker = _workers.GetWorkerConnectionId();
        if (!string.IsNullOrEmpty(worker))
        {
            await Clients.Client(worker).SendAsync("OnRetryDecision", workflowId, retry);
        }
        // Dismiss the prompt on every client viewing this session/workflow.
        await Clients.Group(workflowId.ToString()).SendAsync("ContinuePromptResolved", workflowId);
    }

    // Sends an additional instruction on the same workflow; the worker re-engages the team
    // in the same working folder.
    public async Task SendWorkflowFollowUp(Guid workflowId, string message)
    {
        var worker = _workers.GetWorkerConnectionId();
        if (string.IsNullOrEmpty(worker))
        {
            await ReportWorkflowLog(workflowId, "Error", "Cannot send follow-up: the local PC agent worker is offline.");
            return;
        }

        var wf = await _dbContext.Workflows.FindAsync(workflowId);
        if (wf == null) return;

        wf.Status = "Executing";
        await _dbContext.SaveChangesAsync();

        await ReportWorkflowLog(workflowId, "System", $"➕ Follow-up instruction from user:\n{message}");
        _workers.AssignWorkflow(workflowId, worker);
        await Clients.Client(worker).SendAsync("ExecuteWorkflowFollowUp", workflowId, message, wf.WorkspacePath);
        await Clients.All.SendAsync("ReceiveWorkflowUpdate");
    }

    // The worker reports the actual folder it resolved to work in, so the UI can show it.
    public async Task ReportWorkflowWorkspace(Guid workflowId, string path)
    {
        var wf = await _dbContext.Workflows.FindAsync(workflowId);
        if (wf != null)
        {
            wf.WorkspacePath = path;
            await _dbContext.SaveChangesAsync();
            await BroadcastWorkflowStateAsync(wf);
            await Clients.All.SendAsync("ReceiveWorkflowUpdate");
        }
    }

    // Pushes a workflow's current state to all clients so detail views update in place.
    private Task BroadcastWorkflowStateAsync(TeamWorkflow wf)
    {
        return Clients.All.SendAsync("ReceiveWorkflowState", new WorkflowDto
        {
            Id = wf.Id,
            OriginalTask = wf.OriginalTask,
            AnalystPlan = wf.AnalystPlan,
            EngineerPlan = wf.EngineerPlan,
            TargetTool = wf.TargetTool,
            Status = wf.Status,
            CreatedAt = wf.CreatedAt,
            ExecutedAt = wf.ExecutedAt,
            WorkspacePath = wf.WorkspacePath
        });
    }

    public async Task<List<ModelConfiguration>> GetModelConfigurations()
    {
        return await _dbContext.ModelConfigurations.ToListAsync();
    }

    public async Task<ModelConfiguration> SaveModelConfiguration(ModelConfiguration config)
    {
        var existing = await _dbContext.ModelConfigurations.FindAsync(config.Id);
        if (existing == null)
        {
            _dbContext.ModelConfigurations.Add(config);
        }
        else
        {
            existing.Name = config.Name;
            existing.Provider = config.Provider;
            existing.ModelName = config.ModelName;
            existing.BaseUrl = config.BaseUrl;
            existing.ApiKey = config.ApiKey;
        }
        await _dbContext.SaveChangesAsync();
        return config;
    }

    public async Task DeleteModelConfiguration(Guid id)
    {
        var config = await _dbContext.ModelConfigurations.FindAsync(id);
        if (config != null)
        {
            _dbContext.ModelConfigurations.Remove(config);
            await _dbContext.SaveChangesAsync();
        }
    }
}
