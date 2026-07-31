using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CodingAgents.Shared;
using CodingAgents.Server.Data;
using CodingAgents.Server.Hubs;

namespace CodingAgents.Server.Services;

public class WorkflowManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly WorkerRegistry _workers;

    public WorkflowManager(IServiceProvider serviceProvider, IHubContext<ChatHub> hubContext, WorkerRegistry workers)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _workers = workers;
    }

    public void StartWorkflow(Guid workflowId)
    {
        _ = Task.Run(() => DispatchWorkflowAsync(workflowId));
    }

    public async Task ProcessQueueAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var queuedTasks = await db.Workflows
            .Where(w => w.Status == "Queued")
            .OrderBy(w => w.CreatedAt)
            .ToListAsync();

        if (queuedTasks.Any())
        {
            foreach (var task in queuedTasks)
            {
                _ = Task.Run(() => DispatchWorkflowAsync(task.Id));
            }
        }
    }

    /// <summary>
    /// Re-queues workflows whose owning worker disconnected mid-execution so they can be
    /// picked up by the next available worker instead of being stuck forever.
    /// </summary>
    /// <remarks>
    /// Resuming re-runs the pipeline from the start; the executor step is not idempotent,
    /// so a partially-applied workflow may re-apply some changes on the next run.
    /// </remarks>
    public async Task RequeueInterruptedWorkflowsAsync(IReadOnlyCollection<Guid> workflowIds)
    {
        if (workflowIds.Count == 0) return;

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var settledStatuses = new[] { "Completed", "Failed", "Stalemate", "Queued" };
        bool anyRequeued = false;

        foreach (var workflowId in workflowIds)
        {
            // Only re-queue if still in-flight; if it settled between the disconnect and
            // now (a completion message racing in), leave it as-is.
            int updated = await db.Workflows
                .Where(w => w.Id == workflowId && !settledStatuses.Contains(w.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "Queued"));

            if (updated > 0)
            {
                anyRequeued = true;
                await LogProgressAsync(workflowId, "Queue", "The worker running this workflow disconnected. It has been re-queued and will resume when a worker is available.");
            }
        }

        // If another worker is still connected, dispatch the re-queued work now; otherwise
        // it waits until a worker reconnects and asks the server to drain the queue.
        if (anyRequeued && _workers.IsAnyConnected)
        {
            await ProcessQueueAsync();
        }
    }

    private async Task DispatchWorkflowAsync(Guid workflowId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        // Atomically claim the workflow so it can only be dispatched once, even if both
        // StartWorkflow and ProcessQueueAsync race to pick it up. The conditional update
        // succeeds for exactly one caller; everyone else sees 0 rows affected and bails.
        int claimed = await db.Workflows
            .Where(w => w.Id == workflowId && (w.Status == "Pending" || w.Status == "Queued"))
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "Executing"));

        if (claimed == 0) return;

        try
        {
            var workerConnectionId = _workers.GetWorkerConnectionId();
            if (string.IsNullOrEmpty(workerConnectionId))
            {
                // No worker online right now: put it back on the queue so it runs
                // automatically the next time a worker connects and drains the queue.
                await db.Workflows
                    .Where(w => w.Id == workflowId)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "Queued"));
                await LogProgressAsync(workflowId, "Queue", "Local PC Agent Worker is offline. Task queued; it will run automatically when a worker connects.");
                return;
            }

            var meta = await db.Workflows
                .Where(w => w.Id == workflowId)
                .Select(w => new { w.OriginalTask, w.WorkspacePath })
                .FirstOrDefaultAsync();

            // Record ownership before relaying so a disconnect during/after the send is
            // still attributable to this worker and can be recovered.
            _workers.AssignWorkflow(workflowId, workerConnectionId);

            await LogProgressAsync(workflowId, "System", "Relaying development workflow to local PC worker...");
            await _hubContext.Clients.Client(workerConnectionId).SendAsync("ExecuteWorkflow", workflowId, meta?.OriginalTask ?? string.Empty, meta?.WorkspacePath ?? string.Empty);
        }
        catch (Exception ex)
        {
            _workers.ReleaseWorkflow(workflowId);
            await db.Workflows
                .Where(w => w.Id == workflowId)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, "Failed"));
            await LogProgressAsync(workflowId, "Error", $"Dispatcher Error: {ex.Message}");
        }
    }

    private async Task LogProgressAsync(Guid workflowId, string stage, string message)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

            var log = new WorkflowLog
            {
                WorkflowId = workflowId,
                Stage = stage,
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            db.WorkflowLogs.Add(log);
            await db.SaveChangesAsync();

            await _hubContext.Clients.Group(workflowId.ToString()).SendAsync("ReceiveWorkflowLog", new WorkflowLogDto
            {
                WorkflowId = workflowId,
                Stage = stage,
                Message = message,
                Timestamp = log.Timestamp
            });

            await _hubContext.Clients.All.SendAsync("ReceiveWorkflowUpdate");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error logging workflow progress: {ex.Message}");
        }
    }
}
