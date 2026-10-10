using Microsoft.EntityFrameworkCore;
using PanaszKezelo.Api.Models;

namespace PanaszKezelo.Api.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<CompanyAssessment> CompanyAssessments { get; set; }
        public DbSet<Nis2Requirement> Nis2Requirements { get; set; }
        public DbSet<AssessmentItem> AssessmentItems { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
    }
}