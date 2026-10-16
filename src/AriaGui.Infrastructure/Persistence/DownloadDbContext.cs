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
    public DbSet<BandwidthPolicyRow> BandwidthPolicies => Set<BandwidthPolicyRow>();
    public DbSet<JobBandwidthPolicyRow> JobBandwidthPolicies => Set<JobBandwidthPolicyRow>();
    public DbSet<ExternalRequestRow> ExternalRequests => Set<ExternalRequestRow>();
    public DbSet<PreparedDestinationRow> PreparedDestinations => Set<PreparedDestinationRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<JobBandwidthPolicyRow>().HasKey(row => row.JobId);
        model.Entity<JobBandwidthPolicyRow>().HasOne<JobRow>().WithOne().HasForeignKey<JobBandwidthPolicyRow>(row => row.JobId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<ExternalRequestRow>().HasKey(row => new { row.CallerKey, row.InstallationId, row.RequestId });
        model.Entity<PreparedDestinationRow>().HasKey(row => row.DestinationKey);
        model.Entity<PreparedDestinationRow>().HasOne<ExternalRequestRow>().WithMany()
            .HasForeignKey(row => new { row.CallerKey, row.InstallationId, row.RequestId }).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ExternalRequestRow>().HasIndex(row => row.JobId).IsUnique();
        model.Entity<ExternalRequestRow>().HasIndex(row => new { row.TakeoverPhase, row.PreparedExpiresAtUtcTicks });
        model.Entity<ExternalRequestRow>().HasIndex(row => new { row.CallerKey, row.TakeoverPhase, row.InstallationId });
        model.Entity<ExternalRequestRow>().HasIndex(row => new { row.InstallationId, row.TakeoverPhase });
        model.Entity<ExternalRequestRow>().HasOne<JobRow>().WithMany().HasForeignKey(row => row.JobId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<JobRow>().HasIndex(row => row.DestinationKey).IsUnique();
        model.Entity<AttemptRow>().HasIndex(row => row.EngineId).IsUnique();
        model.Entity<AttemptRow>().Property(row => row.HttpConnections).HasDefaultValue(1);
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
    public string? ExpectedSha256 { get; set; }
    public ChecksumProvenance? ChecksumProvenance { get; set; }
}
public sealed class AttemptRow
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string EngineId { get; set; } = "";
    public long StartedAtUtcTicks { get; set; }
    public Guid? ParentAttemptId { get; set; }
    public int HttpConnections { get; set; } = 1;
    public DownloadStatus Status { get; set; }
    public VerificationStatus Verification { get; set; }
    public long DownloadedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public string? ErrorMessage { get; set; }
    public bool DesiredPaused { get; set; }
    public bool DesiredRemoved { get; set; }
    public bool PolicyHeld { get; set; }
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

public sealed class BandwidthPolicyRow
{
    public int Id { get; set; }
    public long RequestedRevision { get; set; }
    public long ConfirmedRevision { get; set; }
    public string RequestedName { get; set; } = "Unlimited";
    public long? RequestedDownloadBytesPerSecond { get; set; }
    public long? RequestedUploadBytesPerSecond { get; set; }
    public string ConfirmedName { get; set; } = "Unlimited";
    public long? ConfirmedDownloadBytesPerSecond { get; set; }
    public long? ConfirmedUploadBytesPerSecond { get; set; }
    public string? ConfirmedGeneration { get; set; }
    public OperationStatus? RequestStatus { get; set; }
    public string? ScheduleJson { get; set; }
}

public sealed class JobBandwidthPolicyRow
{
    public Guid JobId { get; set; }
    public long RequestedRevision { get; set; }
    public long ConfirmedRevision { get; set; }
    public long? RequestedDownloadBytesPerSecond { get; set; }
    public long? ConfirmedDownloadBytesPerSecond { get; set; }
    public string? ConfirmedGeneration { get; set; }
    public OperationStatus? RequestStatus { get; set; }
}

public sealed class ExternalRequestRow
{
    public string CallerKey { get; set; } = "";
    public Guid InstallationId { get; set; }
    public Guid RequestId { get; set; }
    public string PayloadDigest { get; set; } = "";
    public Guid? JobId { get; set; }
    public AriaGui.Browser.Contracts.ResultCode Code { get; set; }
    public AriaGui.Browser.Contracts.RejectionReason? Reason { get; set; }
    public AriaGui.Application.Downloads.ExternalDispatchPhase DispatchPhase { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
    public long? TombstonedAtUtcTicks { get; set; }
    public AriaGui.Application.Downloads.PreparedTakeoverPhase? TakeoverPhase { get; set; }
    public long? PreparedExpiresAtUtcTicks { get; set; }
    public string? PreparedIntentJson { get; set; }
    public string? PreparedIntentHash { get; set; }
}

public sealed class PreparedDestinationRow
{
    public string DestinationKey { get; set; } = "";
    public string CallerKey { get; set; } = "";
    public Guid InstallationId { get; set; }
    public Guid RequestId { get; set; }
}

// EF tools can generate migrations without launching WinUI or touching the user's database.
public sealed class DownloadDbContextFactory : IDesignTimeDbContextFactory<DownloadDbContext>
{
    public DownloadDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DownloadDbContext>()
        .UseSqlite("Data Source=aria-gui-design-only.db").Options);
}
