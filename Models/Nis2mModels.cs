using System;
using System.Collections.Generic;

namespace PanaszKezelo.Api.Models
{
    // Felhasználói szerepkörök a NIS2 audit rendszerben
    public enum UserRole
    {
        Auditor = 1,       // Kiberbiztonsági auditor / Szakértõ
        CompanyUser = 2,   // Auditált cég képviselõje / Felelõs
        Admin = 3          // Rendszeradminisztrátor
    }

    // A vizsgálat státusza
    public enum AssessmentStatus
    {
        Draft = 1,         // Kérdõív kitöltés alatt
        InProgress = 2,    // Értékelés / AI elemzés folyamatban
        Completed = 3      // Lezárt, auditált állapot
    }

    // Megfelelési szint egy adott követelménynél
    public enum ComplianceStatus
    {
        Compliant = 1,     // Megfelelõ
        PartiallyCompliant = 2, // Részben megfelelõ
        NonCompliant = 3,  // Nem felel meg (hiányosság / Gap)
        NotApplicable = 4  // Nem releváns a cég profiljához
    }

    // 1. Felhasználók
    public class User
    {
        public int Id { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.CompanyUser;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Kapcsolatok
        public List<CompanyAssessment> Assessments { get; set; } = new();
    }

    // 2. Auditált szervezet / Érintettségi profil
    public class CompanyAssessment
    {
        public int Id { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string TaxNumber { get; set; } = string.Empty; // Adószám
        public string Sector { get; set; } = string.Empty;    // Pl. Egészségügy, Energia, IT
        public int EmployeeCount { get; set; }               // Létszám (NIS2 határértékhez)
        public decimal AnnualTurnoverEur { get; set; }       // Éves árbevétel euróban

        public bool IsNis2Affected { get; set; }            // AI által meghatározott érintettség
        public string SectorClassification { get; set; } = string.Empty; // Alapvetõ / Kiemelten kockázatos
        public AssessmentStatus Status { get; set; } = AssessmentStatus.Draft;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Ki hozta létre
        public int UserId { get; set; }
        public User? User { get; set; }

        // Kapcsolódó vizsgálati elemek és naplóbejegyzések
        public List<AssessmentItem> AssessmentItems { get; set; } = new();
        public List<AuditLog> AuditLogs { get; set; } = new();
    }

    // 3. NIS2 Tudásbázis: Követelmények és kontrollok tára (pl. Kibertantv. / SZTFH rendelet alapján)
    public class Nis2Requirement
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;        // Pl. "NIS2-SEC-01", "Kibertan-15-A"
        public string Domain { get; set; } = string.Empty;      // Terület: pl. "Incidenskezelés", "Hozzáférés-védelem"
        public string Title { get; set; } = string.Empty;       // Követelmény címe
        public string Description { get; set; } = string.Empty; // Részletes jogszabályi leírás
        public string LegalReference { get; set; } = string.Empty; // Pl. "2023. évi XXIII. tv. 15. § (1) bek."
    }

    // 4. Egy konkrét követelmény vizsgálata (Itt teljesül az "AI nem fekete doboz" követelmény!)
    public class AssessmentItem
    {
        public int Id { get; set; }

        public int CompanyAssessmentId { get; set; }
        public CompanyAssessment? CompanyAssessment { get; set; }

        public int Nis2RequirementId { get; set; }
        public Nis2Requirement? Nis2Requirement { get; set; }

        // A cég által megadott információ / bizonyíték leírása
        public string CompanyInputOrEvidence { get; set; } = string.Empty;

        // AI ELEMZÉSI EREDMÉNYEK (Kötelezõ tanári elvárások):
        public string AiClaim { get; set; } = string.Empty;           // Mit állít az AI?
        public string AiSourceDocument { get; set; } = string.Empty;  // Mely dokumentumból / jogszabályból származik?
        public string AiConfidence { get; set; } = string.Empty;      // Mennyire biztos az eredmény? (pl. "85%", "Magas")
        public string ExpertCheckRequired { get; set; } = string.Empty; // Mit kell a szakértõnek ellenõriznie?

        public ComplianceStatus Status { get; set; } = ComplianceStatus.NonCompliant;

        // Szakértõi (Auditor) felülbírálat
        public string? AuditorNotes { get; set; }
        public bool IsAuditorApproved { get; set; } = false;
    }

    // 5. Esemény- és auditnapló (Audit Trail)
    public class AuditLog
    {
        public int Id { get; set; }
        public int CompanyAssessmentId { get; set; }
        public CompanyAssessment? CompanyAssessment { get; set; }

        public string Action { get; set; } = string.Empty;     // Pl. "Érintettségi szûrés lefutott", "AI elemzés készült"
        public string PerformedBy { get; set; } = string.Empty;// Ki végezte (Felhasználó neve vagy "AI Engine")
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Details { get; set; } = string.Empty;    // Részletek / Változás leírása
    }
}