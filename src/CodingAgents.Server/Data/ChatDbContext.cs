using Microsoft.EntityFrameworkCore;
using CodingAgents.Shared;

namespace CodingAgents.Server.Data;

public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
    {
    }

    public DbSet<ConversationSession> Sessions => Set<ConversationSession>();
    public DbSet<PersistedMessage> Messages => Set<PersistedMessage>();
    public DbSet<TeamWorkflow> Workflows => Set<TeamWorkflow>();
    public DbSet<WorkflowLog> WorkflowLogs => Set<WorkflowLog>();
    public DbSet<SystemSettings> Settings => Set<SystemSettings>();
    public DbSet<ModelConfiguration> ModelConfigurations => Set<ModelConfiguration>();
    public DbSet<AppCredential> AppCredentials => Set<AppCredential>();
    public DbSet<MemoryFact> MemoryFacts => Set<MemoryFact>();
    public DbSet<MessageEmbedding> MessageEmbeddings => Set<MessageEmbedding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ConversationSession>()
            .HasMany(s => s.Messages)
            .WithOne()
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TeamWorkflow>()
            .HasMany(w => w.Logs)
            .WithOne()
            .HasForeignKey(l => l.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
