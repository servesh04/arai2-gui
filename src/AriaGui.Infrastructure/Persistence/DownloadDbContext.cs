using AriaGui.Core.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AriaGui.Infrastructure.Persistence;

public sealed class DownloadDbContext(DbContextOptions<DownloadDbContext> options) : DbContext(options)
{
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<AttemptRow> Attempts => Set<AttemptRow>();
    public DbSet<FileRow> Files => Set<FileRow>();
    public DbSet<OperationRow> Operations => Set<OperationRow>();
    public DbSet<QueuePolicyRow> QueuePolicies => Set<QueuePolicyRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<JobRow>().HasIndex(row => row.DestinationKey).IsUnique();
        model.Entity<AttemptRow>().HasIndex(row => row.EngineId).IsUnique();
        model.Entity<AttemptRow>().HasOne<JobRow>().WithMany().HasForeignKey(row => row.JobId);
        model.Entity<AttemptRow>().HasOne<AttemptRow>().WithMany().HasForeignKey(row => row.ParentAttemptId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<FileRow>().HasOne<JobRow>().WithMany().HasForeignKey(row => row.JobId);
        model.Entity<FileRow>().HasOne<AttemptRow>().WithMany().HasForeignKey(row => row.AttemptId);
        model.Entity<OperationRow>().HasOne<JobRow>().WithMany().HasForeignKey(row => row.JobId);
        model.Entity<OperationRow>().HasOne<AttemptRow>().WithMany().HasForeignKey(row => row.AttemptId);
        model.Entity<OperationRow>().HasIndex(row => new { row.AttemptId, row.Status });
    }
}

// Database entities deliberately stay separate from immutable domain/runtime records.
public sealed class JobRow
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string DestinationKey { get; set; } = "";
    public long CreatedAtUtcTicks { get; set; }
}
public sealed class AttemptRow
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string EngineId { get; set; } = "";
    public long StartedAtUtcTicks { get; set; }
    public Guid? ParentAttemptId { get; set; }
    public DownloadStatus Status { get; set; }
    public long DownloadedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public string? ErrorMessage { get; set; }
    public bool DesiredPaused { get; set; }
    public bool DesiredRemoved { get; set; }
}
public sealed class FileRow
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid AttemptId { get; set; }
    public string FileName { get; set; } = "";
    public long? TotalBytes { get; set; }
}
public sealed class OperationRow
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid AttemptId { get; set; }
    public DownloadAction Action { get; set; }
    public OperationStatus Status { get; set; }
    public long RequestedAtUtcTicks { get; set; }
    public int? QueuePosition { get; set; }
}
public sealed class QueuePolicyRow
{
    public int Id { get; set; }
    public int Limit { get; set; }
    public int? RequestedLimit { get; set; }
    public Guid? OperationId { get; set; }
    public OperationStatus? RequestStatus { get; set; }
}

// EF tools can generate migrations without launching WinUI or touching the user's database.
public sealed class DownloadDbContextFactory : IDesignTimeDbContextFactory<DownloadDbContext>
{
    public DownloadDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DownloadDbContext>()
        .UseSqlite("Data Source=aria-gui-design-only.db").Options);
}
