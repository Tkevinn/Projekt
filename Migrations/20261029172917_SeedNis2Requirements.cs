using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PanaszKezelo.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeedNis2Requirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Nis2Requirements",
                columns: new[] { "Id", "Code", "Description", "Domain", "LegalReference", "Title" },
                values: new object[,]
                {
                    { 1, "NIS2-POL-01", "A szervezetnek dokumentált, jóváhagyott és rendszeresen felülvizsgált kockázatkezelési módszertannal és információbiztonsági szabályzattal kell rendelkeznie.", "Kockázatelemzés és információbiztonsági szabályzatok", "2023. évi XXIII. tv. (Kibertantv.) 15. § (1) bek. a)", "Kockázatelemzési és információs rendszerek biztonsági szabályzata" },
                    { 2, "NIS2-INC-01", "Kötelező az incidensek megelőzésére, észlelésére, kezelésére és az SZTFH / CSIRT felé történő kötelező 24/72 órás jelentésére vonatkozó eljárásrend kialakítása.", "Incidenskezelés", "2023. évi XXIII. tv. (Kibertantv.) 16. § / NIS2 23. cikk", "Kiberbiztonsági incidensek kezelése és jelentése" },
                    { 3, "NIS2-BCP-01", "Rendszeres, titkosított és elkülönített (lehetőleg offline/offsite) biztonsági mentések készítése, valamint a visszaállítási képesség rendszeres tesztelése.", "Üzletmenet-folytonosság", "2023. évi XXIII. tv. 15. § (1) bek. c)", "Biztonsági mentések és katasztrófa-helyreállítás (BCP/DRP)" },
                    { 4, "NIS2-SEC-01", "Többtényezős hitelesítés (MFA) vagy folyamatos hitelesítési megoldások, védett hang-, video- és szöveges kommunikáció, valamint szükség esetén védett vészhelyzeti kommunikációs rendszerek alkalmazása.", "Hozzáférés-felügyelet és Hitelesítés", "NIS2 Irányelv 21. cikk (2) bek. j)", "Többtényezős hitelesítés (MFA) és hozzáférés-kezelés" },
                    { 5, "NIS2-SUP-01", "A közvetlen szállítók és szolgáltatók által jelentett biztonsági kockázatok felmérése és kezelése a szerződésekben.", "Ellátási lánc biztonsága", "NIS2 Irányelv 21. cikk (2) bek. d)", "Beszállítói kockázatkezelés" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Nis2Requirements",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "Nis2Requirements",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "Nis2Requirements",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "Nis2Requirements",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "Nis2Requirements",
                keyColumn: "Id",
                keyValue: 5);
        }
    }
}
