using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Data;

public sealed class CaseLedgerDbContext(DbContextOptions<CaseLedgerDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<CaseRecord> Cases => Set<CaseRecord>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AuditVerificationJob> AuditVerificationJobs => Set<AuditVerificationJob>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<OperationalReplay> OperationalReplays => Set<OperationalReplay>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<User>();
        user.ToTable("Users");
        user.HasKey(item => item.Id);
        user.Property(item => item.Name).HasMaxLength(100).IsRequired();
        user.Property(item => item.Email).HasMaxLength(200).IsRequired();
        user.Property(item => item.NormalizedEmail).HasMaxLength(200).IsRequired();
        user.Property(item => item.Role).HasMaxLength(40).IsRequired();
        user.Property(item => item.PasswordHash).HasMaxLength(300).IsRequired();
        user.Property(item => item.IsActive).HasDefaultValue(true);
        user.Property(item => item.LocalLoginEnabled).HasDefaultValue(true);
        user.HasIndex(item => item.NormalizedEmail).IsUnique();

        var externalIdentity = modelBuilder.Entity<ExternalIdentity>();
        externalIdentity.ToTable("ExternalIdentities");
        externalIdentity.HasKey(item => item.Id);
        externalIdentity.Property(item => item.Provider).HasMaxLength(32).IsRequired();
        externalIdentity.HasIndex(
                item => new { item.Provider, item.TenantId, item.ObjectId })
            .IsUnique();
        externalIdentity.HasOne(item => item.User)
            .WithMany(item => item.ExternalIdentities)
            .HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var caseRecord = modelBuilder.Entity<CaseRecord>();
        caseRecord.ToTable("Cases");
        caseRecord.HasKey(item => item.Id);
        caseRecord.Property(item => item.Version).IsConcurrencyToken();
        caseRecord.Property(item => item.AuditHeadSequence).HasDefaultValue(0);
        caseRecord.Property(item => item.AuditHeadHash)
            .HasMaxLength(64)
            .IsFixedLength()
            .HasDefaultValue(CaseRecord.GenesisAuditHash)
            .IsRequired();
        caseRecord.Property(item => item.Reference).HasMaxLength(32).IsRequired();
        caseRecord.Property(item => item.Title).HasMaxLength(160).IsRequired();
        caseRecord.Property(item => item.Summary).HasMaxLength(4000).IsRequired();
        caseRecord.Property(item => item.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        caseRecord.Property(item => item.Severity).HasConversion<string>().HasMaxLength(24).IsRequired();
        caseRecord.Property(item => item.Category).HasMaxLength(100).IsRequired();
        caseRecord.Property(item => item.TagsJson).HasColumnType("TEXT").IsRequired();
        caseRecord.HasIndex(item => item.Reference).IsUnique();
        caseRecord.HasIndex(item => item.UpdatedAt);
        caseRecord.HasOne(item => item.Assignee)
            .WithMany()
            .HasForeignKey(item => item.AssigneeId)
            .OnDelete(DeleteBehavior.SetNull);
        caseRecord.HasOne(item => item.CreatedBy)
            .WithMany()
            .HasForeignKey(item => item.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        var evidence = modelBuilder.Entity<Evidence>();
        evidence.ToTable("Evidence");
        evidence.HasKey(item => item.Id);
        evidence.Property(item => item.FileName).HasMaxLength(255).IsRequired();
        evidence.Property(item => item.MediaType).HasMaxLength(150).IsRequired();
        evidence.Property(item => item.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        evidence.HasIndex(item => new { item.CaseId, item.CreatedAt });
        evidence.HasOne(item => item.Case)
            .WithMany(item => item.Evidence)
            .HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Cascade);
        evidence.HasOne(item => item.AddedBy)
            .WithMany()
            .HasForeignKey(item => item.AddedById)
            .OnDelete(DeleteBehavior.Restrict);

        var auditEvent = modelBuilder.Entity<AuditEvent>();
        auditEvent.ToTable("AuditEvents");
        auditEvent.HasKey(item => item.Id);
        auditEvent.Property(item => item.EventType).HasMaxLength(80).IsRequired();
        auditEvent.Property(item => item.Description).HasMaxLength(500).IsRequired();
        auditEvent.Property(item => item.ActorName).HasMaxLength(100).IsRequired();
        auditEvent.Property(item => item.PreviousHash).HasMaxLength(64).IsFixedLength().IsRequired();
        auditEvent.Property(item => item.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
        auditEvent.Property(item => item.CanonicalData).HasColumnType("TEXT").IsRequired();
        auditEvent.HasIndex(item => new { item.CaseId, item.Sequence }).IsUnique();
        auditEvent.HasOne(item => item.Case)
            .WithMany(item => item.AuditEvents)
            .HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Cascade);
        auditEvent.HasOne(item => item.Actor)
            .WithMany()
            .HasForeignKey(item => item.ActorId)
            .OnDelete(DeleteBehavior.Restrict);

        var verificationJob = modelBuilder.Entity<AuditVerificationJob>();
        verificationJob.ToTable("AuditVerificationJobs");
        verificationJob.HasKey(item => item.Id);
        verificationJob.Property(item => item.Status)
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();
        verificationJob.Property(item => item.TargetHash)
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();
        verificationJob.Property(item => item.ResultId).HasMaxLength(100);
        verificationJob.Property(item => item.ChainHead)
            .HasMaxLength(64)
            .IsFixedLength();
        verificationJob.Property(item => item.SnapshotSha256)
            .HasMaxLength(64)
            .IsFixedLength()
            .IsRequired();
        verificationJob.Property(item => item.ErrorCode).HasMaxLength(80);
        verificationJob.HasIndex(item => new { item.CaseId, item.RequestedAt });
        verificationJob.HasIndex(item => new { item.Status, item.RequestedAt });
        verificationJob.HasIndex(item => item.ResultId)
            .IsUnique()
            .HasFilter("\"ResultId\" IS NOT NULL");
        verificationJob.HasOne(item => item.Case)
            .WithMany()
            .HasForeignKey(item => item.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        var outboxMessage = modelBuilder.Entity<OutboxMessage>();
        outboxMessage.ToTable("OutboxMessages");
        outboxMessage.HasKey(item => item.Id);
        outboxMessage.Property(item => item.MessageType).HasMaxLength(160).IsRequired();
        outboxMessage.Property(item => item.PayloadJson).HasColumnType("TEXT").IsRequired();
        outboxMessage.Property(item => item.LastErrorCode).HasMaxLength(80);
        outboxMessage.HasIndex(
            item => new { item.PublishedAt, item.DeadLetteredAt, item.NextAttemptAt });

        var webhookDelivery = modelBuilder.Entity<WebhookDelivery>();
        webhookDelivery.ToTable("WebhookDeliveries");
        webhookDelivery.HasKey(item => item.Id);
        webhookDelivery.Property(item => item.ResultId).HasMaxLength(100).IsRequired();
        webhookDelivery.Property(item => item.EventType).HasMaxLength(160).IsRequired();
        webhookDelivery.Property(item => item.PayloadJson).HasColumnType("TEXT").IsRequired();
        webhookDelivery.Property(item => item.LastErrorCode).HasMaxLength(80);
        webhookDelivery.HasIndex(item => item.VerificationJobId).IsUnique();
        webhookDelivery.HasIndex(item => item.ResultId).IsUnique();
        webhookDelivery.HasIndex(
            item => new { item.DeliveredAt, item.DeadLetteredAt, item.NextAttemptAt });
        webhookDelivery.HasOne(item => item.VerificationJob)
            .WithMany()
            .HasForeignKey(item => item.VerificationJobId)
            .OnDelete(DeleteBehavior.Cascade);

        var operationalReplay = modelBuilder.Entity<OperationalReplay>();
        operationalReplay.ToTable("OperationalReplays");
        operationalReplay.HasKey(item => item.Id);
        operationalReplay.Property(item => item.Kind)
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();
        operationalReplay.Property(item => item.PreviousErrorCode).HasMaxLength(80);
        operationalReplay.Property(item => item.Reason).HasMaxLength(240).IsRequired();
        operationalReplay.HasIndex(
                item => new { item.Kind, item.SourceId, item.SourceDeadLetteredAt })
            .IsUnique();
        operationalReplay.HasIndex(item => item.ReplayedAt);
        operationalReplay.HasOne(item => item.Actor)
            .WithMany()
            .HasForeignKey(item => item.ActorId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareCaseVersions();
        GuardAuditHistory();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PrepareCaseVersions();
        GuardAuditHistory();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareCaseVersions()
    {
        foreach (var entry in ChangeTracker.Entries<CaseRecord>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.Version == Guid.Empty)
                {
                    entry.Entity.Version = Guid.NewGuid();
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.Version = Guid.NewGuid();
            }
        }
    }

    private void GuardAuditHistory()
    {
        var alteredAudit = ChangeTracker.Entries<AuditEvent>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);

        if (alteredAudit is not null)
        {
            throw new InvalidOperationException("Audit events are immutable.");
        }
    }
}
