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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // NIS2 Tudásbázis alapértelmezett követelményei (Seed Data)
            modelBuilder.Entity<Nis2Requirement>().HasData(
                new Nis2Requirement
                {
                    Id = 1,
                    Code = "NIS2-POL-01",
                    Domain = "Kockázatelemzés és információbiztonsági szabályzatok",
                    Title = "Kockázatelemzési és információs rendszerek biztonsági szabályzata",
                    Description = "A szervezetnek dokumentált, jóváhagyott és rendszeresen felülvizsgált kockázatkezelési módszertannal és információbiztonsági szabályzattal kell rendelkeznie.",
                    LegalReference = "2023. évi XXIII. tv. (Kibertantv.) 15. § (1) bek. a)"
                },
                new Nis2Requirement
                {
                    Id = 2,
                    Code = "NIS2-INC-01",
                    Domain = "Incidenskezelés",
                    Title = "Kiberbiztonsági incidensek kezelése és jelentése",
                    Description = "Kötelezõ az incidensek megelõzésére, észlelésére, kezelésére és az SZTFH / CSIRT felé történõ kötelezõ 24/72 órás jelentésére vonatkozó eljárásrend kialakítása.",
                    LegalReference = "2023. évi XXIII. tv. (Kibertantv.) 16. § / NIS2 23. cikk"
                },
                new Nis2Requirement
                {
                    Id = 3,
                    Code = "NIS2-BCP-01",
                    Domain = "Üzletmenet-folytonosság",
                    Title = "Biztonsági mentések és katasztrófa-helyreállítás (BCP/DRP)",
                    Description = "Rendszeres, titkosított és elkülönített (lehetõleg offline/offsite) biztonsági mentések készítése, valamint a visszaállítási képesség rendszeres tesztelése.",
                    LegalReference = "2023. évi XXIII. tv. 15. § (1) bek. c)"
                },
                new Nis2Requirement
                {
                    Id = 4,
                    Code = "NIS2-SEC-01",
                    Domain = "Hozzáférés-felügyelet és Hitelesítés",
                    Title = "Többtényezõs hitelesítés (MFA) és hozzáférés-kezelés",
                    Description = "Többtényezõs hitelesítés (MFA) vagy folyamatos hitelesítési megoldások, védett hang-, video- és szöveges kommunikáció, valamint szükség esetén védett vészhelyzeti kommunikációs rendszerek alkalmazása.",
                    LegalReference = "NIS2 Irányelv 21. cikk (2) bek. j)"
                },
                new Nis2Requirement
                {
                    Id = 5,
                    Code = "NIS2-SUP-01",
                    Domain = "Ellátási lánc biztonsága",
                    Title = "Beszállítói kockázatkezelés",
                    Description = "A közvetlen szállítók és szolgáltatók által jelentett biztonsági kockázatok felmérése és kezelése a szerzõdésekben.",
                    LegalReference = "NIS2 Irányelv 21. cikk (2) bek. d)"
                }
            );
        }
    }
}