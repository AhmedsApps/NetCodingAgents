using System.Collections.Concurrent;

namespace CodingAgents.Server.Services;

/// <summary>
/// Tracks the set of currently-connected local PC worker connections.
/// Replaces the previous single mutable static field so that access is thread-safe,
/// connections are cleaned up on disconnect, and more than one worker can register.
/// </summary>
/// <remarks>
/// This registry is in-memory and therefore per-server-instance. Running the server
/// scaled out to multiple instances would additionally require a SignalR backplane
/// (e.g. Redis) so a worker registered on one instance is reachable from another.
/// </remarks>
public class WorkerRegistry
{
    // Value is the UTC time the connection registered; useful for diagnostics.
    private readonly ConcurrentDictionary<string, DateTime> _workers = new();

    // Maps an in-flight workflow to the worker connection currently running it, so that
    // if the worker disconnects we can identify and recover its orphaned workflows.
    private readonly ConcurrentDictionary<Guid, string> _workflowOwners = new();

    public void Register(string connectionId) => _workers[connectionId] = DateTime.UtcNow;

    public bool Unregister(string connectionId) => _workers.TryRemove(connectionId, out _);

    public bool IsAnyConnected => !_workers.IsEmpty;

    /// <summary>Returns a connection id for an available worker, or null if none are connected.</summary>
    public string? GetWorkerConnectionId() => _workers.Keys.FirstOrDefault();

    public IReadOnlyCollection<string> GetAllConnectionIds() => _workers.Keys.ToArray();

    /// <summary>Records that the given worker connection is now running the workflow.</summary>
    public void AssignWorkflow(Guid workflowId, string connectionId) => _workflowOwners[workflowId] = connectionId;

    /// <summary>Clears ownership for a workflow once it settles or is handed off.</summary>
    public void ReleaseWorkflow(Guid workflowId) => _workflowOwners.TryRemove(workflowId, out _);

    /// <summary>
    /// Removes and returns every workflow owned by the given connection. Called when a
    /// worker disconnects so the caller can re-queue the workflows it left in progress.
    /// </summary>
    public IReadOnlyCollection<Guid> ReleaseWorkflowsForConnection(string connectionId)
    {
        var owned = _workflowOwners
            .Where(kv => kv.Value == connectionId)
            .Select(kv => kv.Key)
            .ToArray();

        foreach (var id in owned)
        {
            _workflowOwners.TryRemove(id, out _);
        }

        return owned;
    }
}
