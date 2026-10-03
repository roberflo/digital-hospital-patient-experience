using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Security.Cryptography;
using System.Text;

namespace Recepcion;

public sealed class TenantScope { public Guid Id { get; set; } }
public abstract class TenantRow { public Guid Id { get; set; } = Guid.NewGuid(); public Guid TenantId { get; set; } }
public sealed class Tenant {
    public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "";
    public string Guide { get; set; } = ""; public bool AgentEnabled { get; set; }
    public string TimeZone { get; set; } = "America/El_Salvador";
    public string? GoogleRefreshToken { get; set; } public string? GoogleCalendarId { get; set; }
    public string? GoogleStateHash { get; set; } public DateTimeOffset? GoogleStateExpires { get; set; }
}
public sealed class Member : TenantRow {
    public string Subject { get; set; } = ""; public string Name { get; set; } = "";
    public string Role { get; set; } = "agent"; public bool Disabled { get; set; }
}
public sealed class Contact : TenantRow {
    public string Name { get; set; } = ""; public string Phone { get; set; } = "";
    public string PhoneHash { get; set; } = ""; public string Email { get; set; } = "";
    public Guid? PatientId { get; set; } public string Tags { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Company : TenantRow { public string Name { get; set; } = ""; public string Industry { get; set; } = ""; public string Email { get; set; } = ""; public string Phone { get; set; } = ""; }
public sealed class Opportunity : TenantRow {
    public string Title { get; set; } = ""; public Guid ContactId { get; set; }
    public decimal Value { get; set; } public string Stage { get; set; } = "new";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Activity : TenantRow {
    public Guid? ContactId { get; set; } public Guid? ConversationId { get; set; }
    public string Kind { get; set; } = "note"; public string Body { get; set; } = "";
    public string Actor { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Channel : TenantRow {
    public string Name { get; set; } = ""; public string PhoneNumberId { get; set; } = "";
    public string? DoctorId { get; set; } public bool Coexistence { get; set; } public bool Enabled { get; set; }
    public string? KapsoCustomerId { get; set; }
}
public sealed class Conversation : TenantRow {
    public Guid ContactId { get; set; } public Guid ChannelId { get; set; }
    public string ExternalId { get; set; } = ""; public string Status { get; set; } = "human";
    public string? AssignedTo { get; set; } public string Summary { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastInboundAt { get; set; }
    public long Revision { get; set; }
}
public sealed class Message : TenantRow {
    public Guid ConversationId { get; set; } public string? ExternalId { get; set; }
    public string Sender { get; set; } = "patient"; public string Body { get; set; } = "";
    public string Type { get; set; } = "text"; public string? MediaId { get; set; }
    public string? MediaName { get; set; } public string Status { get; set; } = "received";
    public string? RequestKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class Job : TenantRow {
    public Guid ConversationId { get; set; } public string Key { get; set; } = "";
    public string Kind { get; set; } = "agent"; public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; } public string? Error { get; set; }
}
public sealed class Receipt : TenantRow { public string Key { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class CalendarLink : TenantRow { public Guid AppointmentId { get; set; } public string GoogleEventId { get; set; } = ""; public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow; }
public sealed class Audit : TenantRow { public string Actor { get; set; } = ""; public string Action { get; set; } = ""; public string Resource { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }

public sealed class CrmDb(DbContextOptions<CrmDb> options, TenantScope scope, IDataProtectionProvider protection) : DbContext(options) {
    public DbSet<Tenant> Tenants => Set<Tenant>(); public DbSet<Member> Members => Set<Member>();
    public DbSet<Contact> Contacts => Set<Contact>(); public DbSet<Company> Companies => Set<Company>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>(); public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Channel> Channels => Set<Channel>(); public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>(); public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Receipt> Receipts => Set<Receipt>(); public DbSet<Audit> Audits => Set<Audit>();
    public DbSet<CalendarLink> CalendarLinks => Set<CalendarLink>();
    protected override void OnModelCreating(ModelBuilder b) {
        Map<Member>(b); Map<Contact>(b); Map<Company>(b); Map<Opportunity>(b); Map<Activity>(b); Map<Channel>(b);
        Map<Conversation>(b); Map<Message>(b); Map<Job>(b); Map<Receipt>(b); Map<Audit>(b); Map<CalendarLink>(b);
        b.Entity<Member>().HasIndex(x=>x.Subject).IsUnique();
        b.Entity<Contact>().HasIndex(x=>new{x.TenantId,x.PhoneHash}).IsUnique();
        b.Entity<Channel>().HasIndex(x=>x.PhoneNumberId).IsUnique();
        b.Entity<Conversation>().HasIndex(x=>new{x.TenantId,x.ChannelId,x.ContactId}).IsUnique();
        b.Entity<Conversation>().Property(x=>x.Revision).IsConcurrencyToken();
        b.Entity<Message>().HasIndex(x=>new{x.TenantId,x.ExternalId}).IsUnique();
        b.Entity<Message>().HasIndex(x=>new{x.TenantId,x.RequestKey}).IsUnique();
        b.Entity<Receipt>().HasIndex(x=>new{x.TenantId,x.Key}).IsUnique();
        b.Entity<Job>().HasIndex(x=>new{x.TenantId,x.Key}).IsUnique();
        b.Entity<CalendarLink>().HasIndex(x=>new{x.TenantId,x.AppointmentId}).IsUnique();
        b.Entity<Opportunity>().Property(x=>x.Value).HasPrecision(14,2);
        b.Entity<Conversation>().HasOne<Contact>().WithMany().HasForeignKey(x=>new{x.TenantId,x.ContactId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Conversation>().HasOne<Channel>().WithMany().HasForeignKey(x=>new{x.TenantId,x.ChannelId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Opportunity>().HasOne<Contact>().WithMany().HasForeignKey(x=>new{x.TenantId,x.ContactId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Message>().HasOne<Conversation>().WithMany().HasForeignKey(x=>new{x.TenantId,x.ConversationId}).HasPrincipalKey(x=>new{x.TenantId,x.Id}).OnDelete(DeleteBehavior.Restrict);
        var p = protection.CreateProtector("recepcion.at-rest.v1");
        var encrypted = new ValueConverter<string,string>(v=>p.Protect(v),v=>p.Unprotect(v));
        b.Entity<Contact>().Property(x=>x.Phone).HasConversion(encrypted); b.Entity<Contact>().Property(x=>x.Name).HasConversion(encrypted);
        b.Entity<Contact>().Property(x=>x.Email).HasConversion(encrypted); b.Entity<Message>().Property(x=>x.Body).HasConversion(encrypted);
        b.Entity<Activity>().Property(x=>x.Body).HasConversion(encrypted); b.Entity<Conversation>().Property(x=>x.Summary).HasConversion(encrypted);
        b.Entity<Tenant>().Property(x=>x.Guide).HasConversion(encrypted); b.Entity<Tenant>().Property(x=>x.GoogleRefreshToken).HasConversion(encrypted!);
    }
    void Map<T>(ModelBuilder b) where T:TenantRow {
        b.Entity<T>().HasKey(x=>x.Id); b.Entity<T>().HasAlternateKey(x=>new{x.TenantId,x.Id});
        b.Entity<T>().HasQueryFilter(x=>x.TenantId==scope.Id);
        b.Entity<T>().HasOne<Tenant>().WithMany().HasForeignKey(x=>x.TenantId).OnDelete(DeleteBehavior.Restrict);
    }
    public override Task<int> SaveChangesAsync(CancellationToken ct=default) {
        foreach(var e in ChangeTracker.Entries<TenantRow>().Where(e=>e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)) {
            if(e.Entity.TenantId!=scope.Id || scope.Id==Guid.Empty) throw new InvalidOperationException("Tenant boundary violation");
            if(e.Entity is Audit && e.State!=EntityState.Added) throw new InvalidOperationException("Audit is append-only");
        }
        return base.SaveChangesAsync(ct);
    }
}
public static class Rules {
    public static string Phone(string phone) {
        var value = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if(value.Length is <8 or >15) throw new ArgumentException("Teléfono internacional inválido"); return value;
    }
    public static string PhoneHash(string phone, string key) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(Phone(phone))));
    public static string Required(string? value,int max=200) => string.IsNullOrWhiteSpace(value)||value.Length>max ? throw new ArgumentException($"Campo requerido (máximo {max} caracteres)") : value.Trim();
    public static bool VerifySignature(byte[] body,string signature,string secret) {
        if(string.IsNullOrWhiteSpace(secret)) return false;
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),body),Convert.FromHexString(signature)); } catch(FormatException) { return false; }
    }
    public static bool WithinWindow(DateTimeOffset? inbound,DateTimeOffset now) => inbound is not null && inbound<=now && now-inbound<TimeSpan.FromHours(24);
    public static readonly string[] Roles = ["platform_admin","admin","supervisor","agent","doctor"];
}
