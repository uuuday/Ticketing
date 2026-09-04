using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Data;

/// <summary>EF Core database context for the ticketing system.</summary>
public class TicketingDbContext : DbContext
{
    public TicketingDbContext(DbContextOptions<TicketingDbContext> options)
        : base(options)
    {
    }

    public DbSet<Event> Events => Set<Event>();
    public DbSet<PricingTier> PricingTiers => Set<PricingTier>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Event>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
            builder.Property(e => e.Venue).IsRequired().HasMaxLength(200);

            builder.HasMany(e => e.PricingTiers)
                .WithOne(t => t.Event)
                .HasForeignKey(t => t.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PricingTier>(builder =>
        {
            builder.HasKey(t => t.Id);
            builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
            builder.Property(t => t.Price).HasPrecision(18, 2);

            builder.ToTable(tb => tb.HasCheckConstraint(
                "CK_PricingTier_NoOversell",
                "[SoldQuantity] >= 0 AND [SoldQuantity] <= [AllocatedQuantity]"));
        });

        modelBuilder.Entity<Order>(builder =>
        {
            builder.HasKey(o => o.Id);
            builder.Property(o => o.IdempotencyKey).IsRequired().HasMaxLength(200);
            builder.Property(o => o.CustomerRef).IsRequired().HasMaxLength(200);
            builder.Property(o => o.TotalAmount).HasPrecision(18, 2);

            builder.HasIndex(o => o.IdempotencyKey).IsUnique();

            builder.HasOne(o => o.Event)
                .WithMany()
                .HasForeignKey(o => o.EventId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasMany(o => o.Lines)
                .WithOne(l => l.Order)
                .HasForeignKey(l => l.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderLine>(builder =>
        {
            builder.HasKey(l => l.Id);
            builder.Property(l => l.UnitPrice).HasPrecision(18, 2);

            builder.HasOne(l => l.PricingTier)
                .WithMany()
                .HasForeignKey(l => l.PricingTierId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
