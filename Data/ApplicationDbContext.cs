using Microsoft.EntityFrameworkCore;
using PanaszKezelo.Api.Models;
using System.Net.Sockets;

namespace PanaszKezelo.Api.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<Ticket> Tickets { get; set; }
        public DbSet<TicketComment> TicketComments { get; set; }
        public DbSet<CompanyPolicy> CompanyPolicies { get; set; }
    }
}