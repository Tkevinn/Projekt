using System;
using System.Collections.Generic;

namespace PanaszKezelo.Api.Models
{
    public enum UserRole
    {
        Employee,
        HRManager,
        Admin
    }

    public enum TicketStatus
    {
        New,
        InReview,
        AutoAnswered,
        Closed
    }

    public class User
    {
        public int Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.Employee;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public List<Ticket> Tickets { get; set; } = new();
    }

    public class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public List<Ticket> Tickets { get; set; } = new();
    }

    public class Ticket
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public TicketStatus Status { get; set; } = TicketStatus.New;
        public bool IsAnonymous { get; set; } = false;
        public string? AiSummary { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int UserId { get; set; }
        public User? User { get; set; }

        public int? CategoryId { get; set; }
        public Category? Category { get; set; }

        public List<TicketComment> Comments { get; set; } = new();
    }

    public class TicketComment
    {
        public int Id { get; set; }
        public string Message { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public bool IsFromAi { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int TicketId { get; set; }
        public Ticket? Ticket { get; set; }
    }

    public class CompanyPolicy
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }
}