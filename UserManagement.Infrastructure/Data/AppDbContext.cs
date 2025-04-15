using Microsoft.EntityFrameworkCore;
using UserManagementApi.Domain.Entities;

namespace UserManagement.Infrastructure.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<User>().HasKey(e => e.Id);
            modelBuilder.Entity<User>().Property(e => e.Email).IsRequired().HasMaxLength(100);
            modelBuilder.Entity<User>().Property(e => e.Username).IsRequired().HasMaxLength(50);
        }


    }
}
