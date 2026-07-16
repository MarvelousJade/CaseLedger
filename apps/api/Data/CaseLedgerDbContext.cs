using CaseLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Data;

public sealed class CaseLedgerDbContext(DbContextOptions<CaseLedgerDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<CaseRecord> Cases => Set<CaseRecord>();
    public DbSet<Evidence> Evidence => Set<Evidence>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

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
        user.HasIndex(item => item.NormalizedEmail).IsUnique();

        var caseRecord = modelBuilder.Entity<CaseRecord>();
        caseRecord.ToTable("Cases");
        caseRecord.HasKey(item => item.Id);
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
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardAuditHistory();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        GuardAuditHistory();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
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
