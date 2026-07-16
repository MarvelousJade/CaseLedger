namespace CaseLedger.Api.Domain;

public enum CaseStatus
{
    New,
    InProgress,
    Resolved
}

public enum CaseSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
}

public sealed class CaseRecord
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public CaseStatus Status { get; set; }
    public CaseSeverity Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public Guid? AssigneeId { get; set; }
    public User? Assignee { get; set; }
    public Guid CreatedById { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DueAt { get; set; }
    public string TagsJson { get; set; } = "[]";
    public ICollection<Evidence> Evidence { get; set; } = new List<Evidence>();
    public ICollection<AuditEvent> AuditEvents { get; set; } = new List<AuditEvent>();
}

public sealed class Evidence
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public CaseRecord Case { get; set; } = null!;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public Guid AddedById { get; set; }
    public User AddedBy { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public CaseRecord Case { get; set; } = null!;
    public int Sequence { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public User Actor { get; set; } = null!;
    public string ActorName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string CanonicalData { get; set; } = string.Empty;
}
