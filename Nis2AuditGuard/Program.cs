using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Adatbázis fájl elérési útja
const string DbConnectionString = "Data Source=nis2_audit.db";

// Adatbázis inicializálása és NIS2 tudásbázis feltöltése induláskor
DatabaseInitializer.Initialize(DbConnectionString);

// ============================================================================
// 1. REST API VÉGPONTOK (Backend szolgáltatások)
// ============================================================================

// Felhasználói bejelentkezés naplózása (Ki és mikor jelentkezett be)
app.MapPost("/api/login", ([FromBody] LoginRequest req) =>
{
    string user = string.IsNullOrWhiteSpace(req.UserName) ? "Névtelen Auditor" : req.UserName.Trim();
    string role = string.IsNullOrWhiteSpace(req.Role) ? "Tanácsadó / Auditor" : req.Role.Trim();
    string actor = $"{user} ({role})";

    AuditLogger.Log(DbConnectionString, "USER_LOGIN", actor,
        $"Felhasználó bejelentkezett a rendszerbe: {actor}",
        JsonSerializer.Serialize(new { UserName = user, Role = role, LoginTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }));

    return Results.Ok(new { Actor = actor, Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
});

// Felhasználói kijelentkezés naplózása
app.MapPost("/api/logout", ([FromBody] LoginRequest req) =>
{
    string actor = string.IsNullOrWhiteSpace(req.UserName) ? "Auditor" : req.UserName.Trim();

    AuditLogger.Log(DbConnectionString, "USER_LOGOUT", actor,
        $"Felhasználó kijelentkezett a rendszerből: {actor}",
        JsonSerializer.Serialize(new { Actor = actor, LogoutTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }));

    return Results.Ok(new { status = "Kijelentkezve" });
});

// Tudásbázis lekérdezése: Jogszabály -> Követelmény -> Kontroll -> Bizonyíték -> Kérdés
app.MapGet("/api/knowledgebase", () =>
{
    var items = KnowledgeBaseService.GetAllRequirementsWithDetails(DbConnectionString);
    return Results.Ok(items);
});

// 1. AI-FUNKCIÓ: NIS2 Követelmény-megfeleltetés és Dokumentumelemzés
app.MapPost("/api/assess", async ([FromBody] AssessmentRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.DocumentContent))
        return Results.BadRequest(new { error = "A dokumentum tartalma nem lehet üres." });

    var requirement = KnowledgeBaseService.GetRequirementById(DbConnectionString, req.RequirementId);
    if (requirement == null)
        return Results.NotFound(new { error = "A kiválasztott NIS2 követelmény nem található." });

    string auditor = string.IsNullOrWhiteSpace(req.AuditorName) ? "Auditor" : req.AuditorName.Trim();

    AuditLogger.Log(DbConnectionString, "ASSESSMENT_START", auditor,
        $"Megfelelőség-vizsgálatot indított: {requirement.ReqCode} | Szervezet: {req.OrganizationName} | Dokumentum: {req.DocumentTitle}",
        JsonSerializer.Serialize(new { req.RequirementId, req.OrganizationName, req.DocumentTitle, InitiatedBy = auditor }));

    var aiResult = await GeminiAiService.AnalyzeComplianceAsync(requirement, req);
    int assessmentId = AssessmentRepository.SaveAssessment(DbConnectionString, req, aiResult);

    AuditLogger.Log(DbConnectionString, "AI_COMPLIANCE_RESULT", $"AI_AGENT [{auditor}]",
        $"AI előzetes értékelés elkészült (ID: #{assessmentId}, Javaslat: {aiResult.SuggestedStatus}, Konfidencia: {aiResult.ConfidenceScore}%)",
        JsonSerializer.Serialize(aiResult));

    return Results.Ok(new { AssessmentId = assessmentId, Result = aiResult, Requirement = requirement });
});

// 2. AI-FUNKCIÓ: Kategorizált Audit- és Ellenőrzési Kérdések Generálása
app.MapPost("/api/generate-questions/{assessmentId:int}", async (int assessmentId, [FromBody] ApiKeyRequest keyReq) =>
{
    var assessment = AssessmentRepository.GetAssessmentById(DbConnectionString, assessmentId);
    if (assessment == null)
        return Results.NotFound(new { error = "Az értékelés nem található." });

    var requirement = KnowledgeBaseService.GetRequirementById(DbConnectionString, assessment.RequirementId);
    if (requirement == null)
        return Results.NotFound(new { error = "A kapcsolódó követelmény nem található." });

    string auditor = string.IsNullOrWhiteSpace(keyReq.AuditorName) ? "Auditor" : keyReq.AuditorName.Trim();

    var questions = await GeminiAiService.GenerateAuditQuestionsAsync(requirement, assessment, keyReq.ApiKey);
    AssessmentRepository.SaveGeneratedQuestions(DbConnectionString, assessmentId, questions);

    AuditLogger.Log(DbConnectionString, "AI_QUESTIONS_GENERATED", auditor,
        $"Kategorizált auditkérdéseket ({questions.Count} db) generáltatott a(z) #{assessmentId} értékeléshez ({assessment.OrganizationName}).",
        JsonSerializer.Serialize(questions));

    var savedQuestions = AssessmentRepository.GetQuestionsByAssessmentId(DbConnectionString, assessmentId);
    return Results.Ok(savedQuestions);
});

// 2/B. AI-FUNKCIÓ: Interaktív Auditinterjú Támogatás
app.MapPost("/api/questions/{questionId:int}/answer", async (int questionId, [FromBody] InterviewAnswerRequest req) =>
{
    var q = AssessmentRepository.GetQuestionById(DbConnectionString, questionId);
    if (q == null)
        return Results.NotFound(new { error = "A kérdés nem található." });

    string auditor = string.IsNullOrWhiteSpace(req.AuditorName) ? "Auditor" : req.AuditorName.Trim();

    string followUp = await GeminiAiService.GenerateFollowUpQuestionAsync(q, req.AuditeeAnswer, req.ApiKey);
    AssessmentRepository.UpdateQuestionAnswer(DbConnectionString, questionId, req.AuditeeAnswer, followUp);

    AuditLogger.Log(DbConnectionString, "INTERVIEW_ANSWER_RECORDED", auditor,
        $"Auditinterjú választ rögzített és AI követő kérdést kért (Kérdés ID: #{questionId}, Kategória: {q.Category}).",
        JsonSerializer.Serialize(new { QuestionId = questionId, req.AuditeeAnswer, FollowUp = followUp, RecordedBy = auditor }));

    return Results.Ok(new { QuestionId = questionId, AuditeeAnswer = req.AuditeeAnswer, AiFollowUpQuestion = followUp });
});

// 3. AI-FUNKCIÓ: Szabályzat-kiegészítési Javaslat és Intézkedési Terv (Remediation) Generálása
app.MapPost("/api/generate-remediation/{assessmentId:int}", async (int assessmentId, [FromBody] ApiKeyRequest keyReq) =>
{
    var assessment = AssessmentRepository.GetAssessmentById(DbConnectionString, assessmentId);
    if (assessment == null)
        return Results.NotFound(new { error = "Az értékelés nem található." });

    var requirement = KnowledgeBaseService.GetRequirementById(DbConnectionString, assessment.RequirementId);
    if (requirement == null)
        return Results.NotFound(new { error = "A kapcsolódó követelmény nem található." });

    string auditor = string.IsNullOrWhiteSpace(keyReq.AuditorName) ? "Auditor" : keyReq.AuditorName.Trim();

    var remediation = await GeminiAiService.GenerateRemediationPlanAsync(requirement, assessment, keyReq.ApiKey);

    AuditLogger.Log(DbConnectionString, "AI_REMEDIATION_GENERATED", $"AI_AGENT [{auditor}]",
        $"AI szabályzat-kiegészítési javaslatot és intézkedési tervet generált a(z) #{assessmentId} értékeléshez ({assessment.OrganizationName}).",
        JsonSerializer.Serialize(remediation));

    return Results.Ok(remediation);
});

// Szakértői felülvizsgálat és végső minősítés jóváhagyása (Human-in-the-loop)
app.MapPost("/api/assessments/{assessmentId:int}/verify", (int assessmentId, [FromBody] ExpertVerificationRequest req) =>
{
    string auditor = string.IsNullOrWhiteSpace(req.AuditorName) ? "Auditor" : req.AuditorName.Trim();

    AssessmentRepository.UpdateExpertDecision(DbConnectionString, assessmentId, req.ExpertFinalStatus, req.ExpertNotes);

    AuditLogger.Log(DbConnectionString, "EXPERT_VERIFICATION", auditor,
        $"Szakértői végső döntést hagyott jóvá (Értékelés ID: #{assessmentId}): '{req.ExpertFinalStatus}' | Megjegyzés: {req.ExpertNotes}",
        JsonSerializer.Serialize(req));

    return Results.Ok(new { status = "Sikeres szakértői jóváhagyás." });
});

// Korábbi értékelések és napló lekérdezése
app.MapGet("/api/assessments", () => Results.Ok(AssessmentRepository.GetAllAssessments(DbConnectionString)));
app.MapGet("/api/logs", () => Results.Ok(AuditLogger.GetLogs(DbConnectionString, 100)));

// EREDMÉNY- ÉS ESEMÉNYNAPLÓ LETÖLTÉSE CSV (Excel) FORMÁTUMBAN
app.MapGet("/api/logs/download/csv", (string? user) =>
{
    string actor = string.IsNullOrWhiteSpace(user) ? "Auditor" : user.Trim();
    AuditLogger.Log(DbConnectionString, "AUDITLOG_DOWNLOAD_CSV", actor,
        "Letöltötte a teljes eredmény- és eseménynaplót CSV (Excel) formátumban.",
        JsonSerializer.Serialize(new { Format = "CSV", DownloadedBy = actor, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }));

    byte[] csvBytes = AuditLogger.ExportLogsAsCsvBytes(DbConnectionString);
    string fileName = $"nis2_eredmeny_es_esemenynaplo_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
    return Results.File(csvBytes, "text/csv; charset=utf-8", fileName);
});

// EREDMÉNY- ÉS ESEMÉNYNAPLÓ LETÖLTÉSE TXT (.log) FORMÁTUMBAN
app.MapGet("/api/logs/download/txt", (string? user) =>
{
    string actor = string.IsNullOrWhiteSpace(user) ? "Auditor" : user.Trim();
    AuditLogger.Log(DbConnectionString, "AUDITLOG_DOWNLOAD_TXT", actor,
        "Letöltötte a teljes eredmény- és eseménynaplót TXT (.log) formátumban.",
        JsonSerializer.Serialize(new { Format = "TXT", DownloadedBy = actor, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }));

    byte[] txtBytes = AuditLogger.ExportLogsAsTxtBytes(DbConnectionString);
    string fileName = $"nis2_eredmeny_es_esemenynaplo_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
    return Results.File(txtBytes, "text/plain; charset=utf-8", fileName);
});

// EREDMÉNY- ÉS ESEMÉNYNAPLÓ LETÖLTÉSE PDF FORMÁTUMBAN (Nyomtatható / PDF mentés nézet)
app.MapGet("/api/logs/download/pdf", (string? user) =>
{
    string actor = string.IsNullOrWhiteSpace(user) ? "Auditor" : user.Trim();
    AuditLogger.Log(DbConnectionString, "AUDITLOG_DOWNLOAD_PDF", actor,
        "Letöltötte / megnyitotta PDF mentésre a teljes eredmény- és eseménynaplót.",
        JsonSerializer.Serialize(new { Format = "PDF", DownloadedBy = actor, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }));

    string html = AuditLogger.ExportLogsAsPrintableHtml(DbConnectionString, actor);
    return Results.Content(html, "text/html; charset=utf-8");
});

// Exportálható HTML/Nyomtatható Audit-előkészítő Jelentés
app.MapGet("/api/export/{assessmentId:int}", (int assessmentId, string? user) =>
{
    string actor = string.IsNullOrWhiteSpace(user) ? "Auditor" : user.Trim();
    AuditLogger.Log(DbConnectionString, "REPORT_EXPORT", actor,
        $"Megnyitotta / exportálta a(z) #{assessmentId} azonosítójú audit-előkészítő jelentést.",
        JsonSerializer.Serialize(new { AssessmentId = assessmentId, ExportedBy = actor }));

    var htmlReport = ReportExportService.GenerateHtmlReport(DbConnectionString, assessmentId);
    if (htmlReport == null) return Results.NotFound("Jelentés nem található.");
    return Results.Content(htmlReport, "text/html; charset=utf-8");
});

// Webes Felhasználói Felület kiszolgálása a gyökérútvonalon
app.MapGet("/", () => Results.Content(WebUiTemplate.GetHtml(), "text/html; charset=utf-8"));

app.Run();

// ============================================================================
// 2. ADATMODELLEK ÉS DTO OSZTÁLYOK
// ============================================================================

public record LoginRequest(string UserName, string Role);

public record RequirementDetailDto(
    int Id, string RegulationCode, string RegulationTitle, string Section,
    string ReqCode, string Domain, string Description, string AffectedRoles,
    List<ControlDto> Controls);

public record ControlDto(
    int Id, string ControlName, string PolicySuggested, string ProcedureSuggested,
    string ResponsibleRole, bool IsMandatoryByLaw, List<string> Evidences, List<string> BaseQuestions);

public record AssessmentRequest(
    int RequirementId, string OrganizationName, string ItEnvironment,
    string DocumentTitle, string DocumentContent, string? ApiKey, string? AuditorName);

public record ApiKeyRequest(string? ApiKey, string? AuditorName);
public record InterviewAnswerRequest(string AuditeeAnswer, string? ApiKey, string? AuditorName);
public record ExpertVerificationRequest(string ExpertFinalStatus, string ExpertNotes, string? AuditorName);

public record AuditLogItemDto(
    int Id, string Timestamp, string EventType, string Actor, string Summary, string RawPayload);

public class AiComplianceResult
{
    [JsonPropertyName("aiClaim")]
    public string AiClaim { get; set; } = "";

    [JsonPropertyName("aiJustification")]
    public string AiJustification { get; set; } = "";

    [JsonPropertyName("documentCitation")]
    public string DocumentCitation { get; set; } = "";

    [JsonPropertyName("mandatoryByLawAnalysis")]
    public string MandatoryByLawAnalysis { get; set; } = "";

    [JsonPropertyName("bestPracticeSuggestions")]
    public string BestPracticeSuggestions { get; set; } = "";

    [JsonPropertyName("confidenceScore")]
    public int ConfidenceScore { get; set; }

    [JsonPropertyName("uncertainties")]
    public string Uncertainties { get; set; } = "";

    [JsonPropertyName("whatExpertMustVerify")]
    public string WhatExpertMustVerify { get; set; } = "";

    [JsonPropertyName("suggestedStatus")]
    public string SuggestedStatus { get; set; } = "Részben felel meg";
}

public class RemediationPlanDto
{
    [JsonPropertyName("suggestedPolicyText")]
    public string SuggestedPolicyText { get; set; } = "";

    [JsonPropertyName("actionSteps")]
    public List<string> ActionSteps { get; set; } = new();

    [JsonPropertyName("responsibleRole")]
    public string ResponsibleRole { get; set; } = "";

    [JsonPropertyName("estimatedDeadline")]
    public string EstimatedDeadline { get; set; } = "";
}

public class GeneratedQuestionDto
{
    public int Id { get; set; }
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";
    [JsonPropertyName("questionText")]
    public string QuestionText { get; set; } = "";
    [JsonPropertyName("targetRole")]
    public string TargetRole { get; set; } = "";
    [JsonPropertyName("expectedEvidence")]
    public string ExpectedEvidence { get; set; } = "";
    public string AuditeeAnswer { get; set; } = "";
    public string AiFollowUpQuestion { get; set; } = "";
}

public class SavedAssessmentDto
{
    public int Id { get; set; }
    public int RequirementId { get; set; }
    public string ReqCode { get; set; } = "";
    public string Domain { get; set; } = "";
    public string RegulationRef { get; set; } = "";
    public string OrganizationName { get; set; } = "";
    public string ItEnvironment { get; set; } = "";
    public string DocumentTitle { get; set; } = "";
    public string DocumentContent { get; set; } = "";
    public AiComplianceResult AiResult { get; set; } = new();
    public string ExpertFinalStatus { get; set; } = "";
    public string ExpertNotes { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

// ============================================================================
// 3. ADATBÁZIS INICIALIZÁLÓ ÉS STRUKTURÁLT NIS2 TUDÁSBÁZIS (SQLite)
// ============================================================================

public static class DatabaseInitializer
{
    public static void Initialize(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Regulations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Code TEXT NOT NULL,
                Title TEXT NOT NULL,
                Section TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Requirements (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RegulationId INTEGER NOT NULL,
                ReqCode TEXT NOT NULL,
                Domain TEXT NOT NULL,
                Description TEXT NOT NULL,
                AffectedRoles TEXT NOT NULL,
                FOREIGN KEY(RegulationId) REFERENCES Regulations(Id)
            );

            CREATE TABLE IF NOT EXISTS Controls (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RequirementId INTEGER NOT NULL,
                ControlName TEXT NOT NULL,
                PolicySuggested TEXT NOT NULL,
                ProcedureSuggested TEXT NOT NULL,
                ResponsibleRole TEXT NOT NULL,
                IsMandatoryByLaw INTEGER NOT NULL,
                FOREIGN KEY(RequirementId) REFERENCES Requirements(Id)
            );

            CREATE TABLE IF NOT EXISTS Evidences (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ControlId INTEGER NOT NULL,
                EvidenceName TEXT NOT NULL,
                ExpectedContent TEXT NOT NULL,
                FOREIGN KEY(ControlId) REFERENCES Controls(Id)
            );

            CREATE TABLE IF NOT EXISTS BaseQuestions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ControlId INTEGER NOT NULL,
                Category TEXT NOT NULL,
                QuestionText TEXT NOT NULL,
                FOREIGN KEY(ControlId) REFERENCES Controls(Id)
            );

            CREATE TABLE IF NOT EXISTS Assessments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RequirementId INTEGER NOT NULL,
                OrganizationName TEXT NOT NULL,
                ItEnvironment TEXT NOT NULL,
                DocumentTitle TEXT NOT NULL,
                DocumentContent TEXT NOT NULL,
                AiClaim TEXT NOT NULL,
                AiJustification TEXT NOT NULL,
                DocumentCitation TEXT NOT NULL,
                MandatoryElements TEXT NOT NULL,
                BestPracticeSuggestions TEXT NOT NULL,
                ConfidenceScore INTEGER NOT NULL,
                Uncertainties TEXT NOT NULL,
                WhatExpertMustVerify TEXT NOT NULL,
                AiSuggestedStatus TEXT NOT NULL,
                ExpertFinalStatus TEXT NOT NULL,
                ExpertNotes TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY(RequirementId) REFERENCES Requirements(Id)
            );

            CREATE TABLE IF NOT EXISTS GeneratedQuestions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AssessmentId INTEGER NOT NULL,
                Category TEXT NOT NULL,
                QuestionText TEXT NOT NULL,
                TargetRole TEXT NOT NULL,
                ExpectedEvidence TEXT NOT NULL,
                AuditeeAnswer TEXT NOT NULL,
                AiFollowUpQuestion TEXT NOT NULL,
                FOREIGN KEY(AssessmentId) REFERENCES Assessments(Id)
            );

            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Actor TEXT NOT NULL,
                Summary TEXT NOT NULL,
                RawPayload TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM Regulations;";
        long count = (long)(checkCmd.ExecuteScalar() ?? 0L);

        if (count == 0)
        {
            SeedKnowledgeBase(conn);
        }
    }

    private static void SeedKnowledgeBase(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = """
            INSERT INTO Regulations (Id, Code, Title, Section) VALUES
            (1, '2024. évi LXIX. törvény & 7/2024. (VI. 24.) MK rendelet', 'Magyarország kiberbiztonságáról szóló törvény és a biztonsági osztályba sorolás követelményei', '2. melléklet - Védelmi intézkedések katalógusa');

            INSERT INTO Requirements (Id, RegulationId, ReqCode, Domain, Description, AffectedRoles) VALUES
            (1, 1, 'NIS2-AC-01', 'Hozzáférés- és jogosultságkezelés', 'A szervezetnek biztosítania kell a felhasználói és rendszergazdai hozzáférések szabályozott kiosztását, módosítását, visszavonását, a legkisebb jogosultság elvét, valamint a jogosultságok rendszeres (legalább évenkénti) felülvizsgálatát.', 'IT / IBF / HR / Vezetés'),
            (2, 1, 'NIS2-BC-02', 'Mentés és üzletmenet-folytonosság', 'A szervezetnek gondoskodnia kell a kritikus rendszerek és adatok rendszeres biztonsági mentéséről, a mentések elkülönített tárolásáról, valamint a visszaállítási eljárások rendszeres teszteléséről és dokumentálásáról.', 'IT / IBF / Vezetés'),
            (3, 1, 'NIS2-IR-03', 'Incidenskezelés és naplózás', 'A szervezetnek dokumentált incidenskezelési eljárásrenddel, biztonsági eseménynaplózással és az SZTFH / CSIRT felé történő kötelező bejelentési folyamattal (24 órás korai figyelmeztetés, 72 órás bejelentés) kell rendelkeznie.', 'IBF / IT / Vezetés');

            INSERT INTO Controls (Id, RequirementId, ControlName, PolicySuggested, ProcedureSuggested, ResponsibleRole, IsMandatoryByLaw) VALUES
            (1, 1, 'Belépő/kilépő jogosultság-kezelési folyamat és jogosultság-felülvizsgálat', 'Hozzáférés-kezelési szabályzat', 'Belépési/kilépési (Onboarding/Offboarding) és időszakos jogosultság-felülvizsgálati eljárásrend', 'IBF és IT Vezető', 1),
            (2, 1, 'Kiemelt (rendszergazdai) fiókok elkülönítése és MFA kötelezettség', 'Privilegizált hozzáférés-kezelési szabályzat', 'Rendszergazdai jogosultságok nyilvántartása és többtényezős hitelesítés (MFA) kikényszerítése', 'IT Vezető', 1),
            (3, 1, 'Automatizált IAM / Active Directory jogosultság-audit szkript alkalmazása', 'IT Üzemeltetési Kézikönyv', 'Havi automatizált inaktív fiók-lekérdezés és zárolás', 'Rendszergazda', 0),
            (4, 2, 'Rendszeres biztonsági mentés és visszaállítási teszt', 'Mentési szabályzat és Üzletmenet-folytonossági terv (BCP)', 'Mentési és visszaállítási tesztelési eljárásrend', 'IT Vezető', 1),
            (5, 3, 'Kiberbiztonsági incidenskezelés és hatósági bejelentés', 'Incidenskezelési szabályzat', 'Incidens osztályozási és CSIRT bejelentési munkafolyamat', 'IBF (Információbiztonsági Felelős)', 1);

            INSERT INTO Evidences (ControlId, EvidenceName, ExpectedContent) VALUES
            (1, 'Felhasználói jogosultságlista és AD export', 'Aktív felhasználók, csoporttagságok és utolsó belépési idők tételes listája.'),
            (1, 'Időszakos jogosultság-felülvizsgálati jegyzőkönyv', 'Évenkénti felülvizsgálat dátuma, résztvevői és a visszavont jogosultságok igazolása.'),
            (2, 'Rendszergazdai jogosultságok nyilvántartása és MFA napló', 'Kiemelt fiókok listája, névre szóló hozzárendelés és MFA státusz.'),
            (4, 'Mentési napló és Visszaállítási tesztjegyzőkönyv', 'Sikeres mentési futások naplója és a visszaállítási teszt dokumentált eredménye.'),
            (5, 'Incidensnapló és Bejelentési sablon', 'Regisztrált biztonsági események és azok kivizsgálási dokumentációja.');

            INSERT INTO BaseQuestions (ControlId, Category, QuestionText) VALUES
            (1, 'Alapkérdés', 'Hogyan történik egy munkavállaló jogosultságainak megszüntetése kilépéskor?'),
            (1, 'Mélyítő kérdés', 'Ki kezdeményezi a jogosultság megszüntetését, és mennyi idő telik el a HR kiléptetés és az AD fiók zárolása között?'),
            (1, 'Bizonyítékot bekérő kérdés', 'Milyen jegyzőkönyv vagy napló igazolja az elmúlt 12 hónapban elvégzett teljes körű jogosultság-felülvizsgálatot?'),
            (4, 'Bizonyítékot bekérő kérdés', 'Milyen bizonyíték igazolja, hogy a biztonsági mentésekből a visszaállítást ténylegesen tesztelték az elmúlt évben?');
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}

// ============================================================================
// 4. TUDÁSBÁZIS ÉS ADATELÉRÉSI SZOLGÁLTATÁSOK
// ============================================================================

public static class KnowledgeBaseService
{
    public static List<RequirementDetailDto> GetAllRequirementsWithDetails(string connStr)
    {
        var list = new List<RequirementDetailDto>();
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.Id, reg.Code, reg.Title, reg.Section, r.ReqCode, r.Domain, r.Description, r.AffectedRoles
            FROM Requirements r
            JOIN Regulations reg ON r.RegulationId = reg.Id
            ORDER BY r.Id;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int reqId = reader.GetInt32(0);
            var controls = GetControlsForRequirement(conn, reqId);
            list.Add(new RequirementDetailDto(
                reqId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                controls));
        }
        return list;
    }

    public static RequirementDetailDto? GetRequirementById(string connStr, int reqId)
    {
        return GetAllRequirementsWithDetails(connStr).FirstOrDefault(r => r.Id == reqId);
    }

    private static List<ControlDto> GetControlsForRequirement(SqliteConnection conn, int reqId)
    {
        var controls = new List<ControlDto>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ControlName, PolicySuggested, ProcedureSuggested, ResponsibleRole, IsMandatoryByLaw
            FROM Controls WHERE RequirementId = $reqId;
            """;
        cmd.Parameters.AddWithValue("$reqId", reqId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int ctrlId = reader.GetInt32(0);
            controls.Add(new ControlDto(
                ctrlId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5) == 1,
                GetSimpleList(conn, "SELECT EvidenceName || ' (' || ExpectedContent || ')' FROM Evidences WHERE ControlId = $id", ctrlId),
                GetSimpleList(conn, "SELECT '[' || Category || '] ' || QuestionText FROM BaseQuestions WHERE ControlId = $id", ctrlId)
            ));
        }
        return controls;
    }

    private static List<string> GetSimpleList(SqliteConnection conn, string sql, int id)
    {
        var result = new List<string>();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }
}

public static class AssessmentRepository
{
    public static int SaveAssessment(string connStr, AssessmentRequest req, AiComplianceResult ai)
    {
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Assessments (
                RequirementId, OrganizationName, ItEnvironment, DocumentTitle, DocumentContent,
                AiClaim, AiJustification, DocumentCitation, MandatoryElements, BestPracticeSuggestions,
                ConfidenceScore, Uncertainties, WhatExpertMustVerify, AiSuggestedStatus,
                ExpertFinalStatus, ExpertNotes, CreatedAt
            ) VALUES (
                $reqId, $org, $env, $docTitle, $docContent,
                $claim, $just, $cit, $mand, $best,
                $conf, $unc, $verify, $aiStatus,
                $expStatus, $expNotes, $createdAt
            );
            SELECT last_insert_rowid();
            """;

        cmd.Parameters.AddWithValue("$reqId", req.RequirementId);
        cmd.Parameters.AddWithValue("$org", req.OrganizationName);
        cmd.Parameters.AddWithValue("$env", req.ItEnvironment ?? "Általános vállalati IT környezet");
        cmd.Parameters.AddWithValue("$docTitle", req.DocumentTitle);
        cmd.Parameters.AddWithValue("$docContent", req.DocumentContent);
        cmd.Parameters.AddWithValue("$claim", ai.AiClaim);
        cmd.Parameters.AddWithValue("$just", ai.AiJustification);
        cmd.Parameters.AddWithValue("$cit", ai.DocumentCitation);
        cmd.Parameters.AddWithValue("$mand", ai.MandatoryByLawAnalysis);
        cmd.Parameters.AddWithValue("$best", ai.BestPracticeSuggestions);
        cmd.Parameters.AddWithValue("$conf", ai.ConfidenceScore);
        cmd.Parameters.AddWithValue("$unc", ai.Uncertainties);
        cmd.Parameters.AddWithValue("$verify", ai.WhatExpertMustVerify);
        cmd.Parameters.AddWithValue("$aiStatus", ai.SuggestedStatus);
        cmd.Parameters.AddWithValue("$expStatus", "Felülvizsgálatra vár (Tervezet)");
        cmd.Parameters.AddWithValue("$expNotes", "");
        cmd.Parameters.AddWithValue("$createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static SavedAssessmentDto? GetAssessmentById(string connStr, int id)
    {
        return GetAllAssessments(connStr).FirstOrDefault(a => a.Id == id);
    }

    public static List<SavedAssessmentDto> GetAllAssessments(string connStr)
    {
        var list = new List<SavedAssessmentDto>();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.Id, a.RequirementId, r.ReqCode, r.Domain, reg.Code || ' (' || reg.Section || ')',
                   a.OrganizationName, a.ItEnvironment, a.DocumentTitle, a.DocumentContent,
                   a.AiClaim, a.AiJustification, a.DocumentCitation, a.MandatoryElements, a.BestPracticeSuggestions,
                   a.ConfidenceScore, a.Uncertainties, a.WhatExpertMustVerify, a.AiSuggestedStatus,
                   a.ExpertFinalStatus, a.ExpertNotes, a.CreatedAt
            FROM Assessments a
            JOIN Requirements r ON a.RequirementId = r.Id
            JOIN Regulations reg ON r.RegulationId = reg.Id
            ORDER BY a.Id DESC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SavedAssessmentDto
            {
                Id = reader.GetInt32(0),
                RequirementId = reader.GetInt32(1),
                ReqCode = reader.GetString(2),
                Domain = reader.GetString(3),
                RegulationRef = reader.GetString(4),
                OrganizationName = reader.GetString(5),
                ItEnvironment = reader.GetString(6),
                DocumentTitle = reader.GetString(7),
                DocumentContent = reader.GetString(8),
                AiResult = new AiComplianceResult
                {
                    AiClaim = reader.GetString(9),
                    AiJustification = reader.GetString(10),
                    DocumentCitation = reader.GetString(11),
                    MandatoryByLawAnalysis = reader.GetString(12),
                    BestPracticeSuggestions = reader.GetString(13),
                    ConfidenceScore = reader.GetInt32(14),
                    Uncertainties = reader.GetString(15),
                    WhatExpertMustVerify = reader.GetString(16),
                    SuggestedStatus = reader.GetString(17)
                },
                ExpertFinalStatus = reader.GetString(18),
                ExpertNotes = reader.GetString(19),
                CreatedAt = reader.GetString(20)
            });
        }
        return list;
    }

    public static void UpdateExpertDecision(string connStr, int assessmentId, string finalStatus, string notes)
    {
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Assessments SET ExpertFinalStatus = $status, ExpertNotes = $notes WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$status", finalStatus);
        cmd.Parameters.AddWithValue("$notes", notes ?? "");
        cmd.Parameters.AddWithValue("$id", assessmentId);
        cmd.ExecuteNonQuery();
    }

    public static void SaveGeneratedQuestions(string connStr, int assessmentId, List<GeneratedQuestionDto> questions)
    {
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var tx = conn.BeginTransaction();

        var delCmd = conn.CreateCommand();
        delCmd.Transaction = tx;
        delCmd.CommandText = "DELETE FROM GeneratedQuestions WHERE AssessmentId = $id;";
        delCmd.Parameters.AddWithValue("$id", assessmentId);
        delCmd.ExecuteNonQuery();

        foreach (var q in questions)
        {
            var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO GeneratedQuestions (AssessmentId, Category, QuestionText, TargetRole, ExpectedEvidence, AuditeeAnswer, AiFollowUpQuestion)
                VALUES ($aId, $cat, $qText, $role, $ev, '', '');
                """;
            cmd.Parameters.AddWithValue("$aId", assessmentId);
            cmd.Parameters.AddWithValue("$cat", q.Category);
            cmd.Parameters.AddWithValue("$qText", q.QuestionText);
            cmd.Parameters.AddWithValue("$role", q.TargetRole);
            cmd.Parameters.AddWithValue("$ev", q.ExpectedEvidence);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static List<GeneratedQuestionDto> GetQuestionsByAssessmentId(string connStr, int assessmentId)
    {
        var list = new List<GeneratedQuestionDto>();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Category, QuestionText, TargetRole, ExpectedEvidence, AuditeeAnswer, AiFollowUpQuestion
            FROM GeneratedQuestions WHERE AssessmentId = $id ORDER BY Id;
            """;
        cmd.Parameters.AddWithValue("$id", assessmentId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new GeneratedQuestionDto
            {
                Id = r.GetInt32(0),
                Category = r.GetString(1),
                QuestionText = r.GetString(2),
                TargetRole = r.GetString(3),
                ExpectedEvidence = r.GetString(4),
                AuditeeAnswer = r.GetString(5),
                AiFollowUpQuestion = r.GetString(6)
            });
        }
        return list;
    }

    public static GeneratedQuestionDto? GetQuestionById(string connStr, int questionId)
    {
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Category, QuestionText, TargetRole, ExpectedEvidence, AuditeeAnswer, AiFollowUpQuestion
            FROM GeneratedQuestions WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", questionId);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            return new GeneratedQuestionDto
            {
                Id = r.GetInt32(0),
                Category = r.GetString(1),
                QuestionText = r.GetString(2),
                TargetRole = r.GetString(3),
                ExpectedEvidence = r.GetString(4),
                AuditeeAnswer = r.GetString(5),
                AiFollowUpQuestion = r.GetString(6)
            };
        }
        return null;
    }

    public static void UpdateQuestionAnswer(string connStr, int questionId, string answer, string followUp)
    {
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE GeneratedQuestions SET AuditeeAnswer = $ans, AiFollowUpQuestion = $fup WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$ans", answer);
        cmd.Parameters.AddWithValue("$fup", followUp);
        cmd.Parameters.AddWithValue("$id", questionId);
        cmd.ExecuteNonQuery();
    }
}

// ============================================================================
// 5. ESEMÉNY- ÉS DÖNTÉSNAPLÓZÓ SZOLGÁLTATÁS + NAPLÓLETÖLTÉS (CSV / TXT / PDF)
// ============================================================================

public static class AuditLogger
{
    public static void Log(string connStr, string eventType, string actor, string summary, string rawPayload)
    {
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AuditLogs (Timestamp, EventType, Actor, Summary, RawPayload)
            VALUES ($ts, $type, $actor, $sum, $raw);
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$type", eventType);
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$sum", summary);
        cmd.Parameters.AddWithValue("$raw", rawPayload);
        cmd.ExecuteNonQuery();
    }

    public static List<AuditLogItemDto> GetLogs(string connStr, int limit = 100)
    {
        var list = new List<AuditLogItemDto>();
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Timestamp, EventType, Actor, Summary, RawPayload FROM AuditLogs ORDER BY Id DESC LIMIT {limit};";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditLogItemDto(
                r.GetInt32(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5)
            ));
        }
        return list;
    }

    public static byte[] ExportLogsAsCsvBytes(string connStr)
    {
        var logs = GetLogs(connStr, 10000);
        var sb = new StringBuilder();

        // pontosvesszővel tagolt fejléc a magyar Excelhez
        sb.AppendLine("Azonosito;Idopont (Mikor);Felhasznalo / Aktor (Ki);Esemeny tipusa;Tevekenyseg es eredmeny leirasa (Mit csinalt);Reszletes technikai adatok (JSON)");

        foreach (var l in logs)
        {
            sb.AppendLine($"{l.Id};{EscapeCsv(l.Timestamp)};{EscapeCsv(l.Actor)};{EscapeCsv(l.EventType)};{EscapeCsv(l.Summary)};{EscapeCsv(l.RawPayload)}");
        }

        // UTF-8 BOM hozzáadása, hogy a magyar ékezetes karakterek Excelben is hibátlanul jelenjenek meg
        byte[] bom = Encoding.UTF8.GetPreamble();
        byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] result = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(body, 0, result, bom.Length, body.Length);
        return result;
    }

    public static byte[] ExportLogsAsTxtBytes(string connStr)
    {
        var logs = GetLogs(connStr, 10000);
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("NIS2-AUDITGUARD AI - EREDMÉNY- ÉS ESEMÉNYNAPLÓ (AUDIT LOG EXPORT)");
        sb.AppendLine($"Exportálás időpontja: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Tartalom: Felhasználói be-/kijelentkezések, AI döntések és szakértői műveletek");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        foreach (var l in logs)
        {
            sb.AppendLine($"[#{l.Id}] IDŐPONT: {l.Timestamp}");
            sb.AppendLine($"  KI (FELHASZNÁLÓ / AKTOR): {l.Actor}");
            sb.AppendLine($"  ESEMÉNY TÍPUSA:           {l.EventType}");
            sb.AppendLine($"  MIT CSINÁLT / EREDMÉNY:   {l.Summary}");
            sb.AppendLine($"  RÉSZLETES ADATOK (JSON):  {l.RawPayload}");
            sb.AppendLine("--------------------------------------------------------------------------------");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static string ExportLogsAsPrintableHtml(string connStr, string exportedBy)
    {
        var logs = GetLogs(connStr, 10000);
        var rows = new StringBuilder();
        foreach (var l in logs)
        {
            rows.AppendLine($"""
                <tr>
                    <td>#{l.Id}</td>
                    <td>{System.Net.WebUtility.HtmlEncode(l.Timestamp)}</td>
                    <td><strong>{System.Net.WebUtility.HtmlEncode(l.Actor)}</strong></td>
                    <td><code>{System.Net.WebUtility.HtmlEncode(l.EventType)}</code></td>
                    <td>{System.Net.WebUtility.HtmlEncode(l.Summary)}</td>
                </tr>
                """);
        }

        return $$"""
            <!DOCTYPE html>
            <html lang='hu'>
            <head>
                <meta charset='UTF-8'>
                <title>NIS2-AuditGuard AI - Eredmény- és Eseménynapló (PDF)</title>
                <style>
                    body { font-family: Arial, sans-serif; margin: 30px; color: #1e293b; font-size: 13px; }
                    h1 { color: #0f172a; margin-bottom: 6px; font-size: 20px; }
                    .meta { color: #475569; margin-bottom: 18px; }
                    table { width: 100%; border-collapse: collapse; }
                    th, td { border: 1px solid #cbd5e1; padding: 7px 9px; text-align: left; vertical-align: top; }
                    th { background: #e2e8f0; font-weight: bold; }
                    tr:nth-child(even) { background: #f8fafc; }
                    @media print { .no-print { display: none; } }
                </style>
            </head>
            <body onload="window.print()">
                <button class="no-print" onclick="window.print()" style="padding:8px 14px; margin-bottom:14px; cursor:pointer;">🖨️ Nyomtatás / Mentés PDF-ként</button>
                <h1>NIS2-AuditGuard AI – Eredmény- és Eseménynapló</h1>
                <div class="meta">
                    <strong>Exportálás időpontja:</strong> {{DateTime.Now:yyyy-MM-dd HH:mm:ss}} |
                    <strong>Exportálta:</strong> {{System.Net.WebUtility.HtmlEncode(exportedBy)}} |
                    <strong>Összes bejegyzés:</strong> {{logs.Count}} db
                </div>
                <table>
                    <thead>
                        <tr>
                            <th style="width:55px;">ID</th>
                            <th style="width:145px;">Időpont</th>
                            <th style="width:210px;">Felhasználó / Aktor</th>
                            <th style="width:180px;">Esemény típusa</th>
                            <th>Tevékenység és eredmény leírása</th>
                        </tr>
                    </thead>
                    <tbody>
                        {{rows}}
                    </tbody>
                </table>
            </body>
            </html>
            """;
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        string cleaned = value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");
        return $"\"{cleaned}\"";
    }
}

// ============================================================================
// 6. GOOGLE GEMINI AI SZOLGÁLTATÁS ("FEHÉR DOBOZ" + JSON SCHEMA)
// ============================================================================

public static class GeminiAiService
{
    private static readonly HttpClient Http = new();
    private const string GeminiModel = "gemini-2.5-flash";

    public static async Task<AiComplianceResult> AnalyzeComplianceAsync(RequirementDetailDto reqDetail, AssessmentRequest req)
    {
        string apiKey = !string.IsNullOrWhiteSpace(req.ApiKey)
            ? req.ApiKey
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";

        var kbContext = BuildKnowledgeBasePromptContext(reqDetail);

        string systemPrompt = $"""
            Te egy magyarországi NIS2 kiberbiztonsági tanácsadó és audit-előkészítő AI asszisztens vagy.
            FELADATOD: Vesd össze a felhasználó által feltöltött vállalati szabályzatot a megadott strukturált NIS2 tudásbázis követelményével.
            SZIGORÚ SZABÁLYOK (Hallucináció-mentesség és Fehér doboz működés):
            1. Kizárólag a megadott NIS2 tudásbázis jogszabályi hivatkozásait használd, tilos jogszabályt kitalálni!
            2. A 'documentCitation' mezőben szó szerint idézd a feltöltött dokumentum releváns szakaszát/oldalát, vagy írd le pontosan, melyik rendelkezés hiányzik belőle!
            3. Különböztesd meg egyértelműen, hogy mit ír elő kötelezően a jogszabály ('mandatoryByLawAnalysis'), és mit javasolsz te jó gyakorlatként ('bestPracticeSuggestions')!
            4. Adj meg egy 0-100 közötti bizonyossági értéket ('confidenceScore'), sorold fel a hiányzó információkat ('uncertainties'), és írd le, mit kell a humán auditornak ellenőriznie ('whatExpertMustVerify').

            STRUKTURÁLT NIS2 TUDÁSBÁZIS KONTEXTUS:
            {kbContext}

            VIZSGÁLT SZERVEZET: {req.OrganizationName} (IT környezet: {req.ItEnvironment})
            FELTÖLTÖTT DOKUMENTUM CÍME: {req.DocumentTitle}
            FELTÖLTÖTT DOKUMENTUM TARTALMA:
            {req.DocumentContent}
            """;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                string jsonResponse = await CallGeminiApiAsync(apiKey, systemPrompt);
                var parsed = JsonSerializer.Deserialize<AiComplianceResult>(jsonResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.AiClaim))
                    return parsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gemini API hívási figyelmeztetés: {ex.Message}");
            }
        }

        return RunLocalDeterministicAnalysis(reqDetail, req);
    }

    public static async Task<List<GeneratedQuestionDto>> GenerateAuditQuestionsAsync(
        RequirementDetailDto reqDetail, SavedAssessmentDto assessment, string? userApiKey)
    {
        string apiKey = !string.IsNullOrWhiteSpace(userApiKey)
            ? userApiKey
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";

        string prompt = $"""
            Te egy NIS2 kiberbiztonsági auditor asszisztens vagy.
            Készíts pontosan 4 darab, egymásra épülő auditinterjú-kérdést a feltárt hiányosságok és a NIS2 követelmény alapján!
            A 4 kérdésnek kötelezően az alábbi 4 kategóriát kell lefednie ('category' mező):
            1. 'Alapkérdés'
            2. 'Mélyítő kérdés'
            3. 'Bizonyítékot bekérő kérdés'
            4. 'Ellenőrző kérdés'

            NIS2 KÖVETELMÉNY: {reqDetail.ReqCode} - {reqDetail.Domain}: {reqDetail.Description}
            SZERVEZET ÉS IT KÖRNYEZET: {assessment.OrganizationName} ({assessment.ItEnvironment})
            ELŐZETES DOKUMENTUMELEMZÉS EREDMÉNYE: {assessment.AiResult.AiClaim}
            FELTÁRT BIZONYTALANSÁG / HIÁNYOSSÁG: {assessment.AiResult.Uncertainties}

            Válaszolj kizárólag JSON tömbként, amelynek elemei: category, questionText, targetRole, expectedEvidence.
            """;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                string jsonResponse = await CallGeminiApiAsync(apiKey, prompt);
                var parsed = JsonSerializer.Deserialize<List<GeneratedQuestionDto>>(jsonResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (parsed != null && parsed.Count > 0)
                    return parsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gemini API kérdésgenerálási figyelmeztetés: {ex.Message}");
            }
        }

        return new List<GeneratedQuestionDto>
        {
            new()
            {
                Category = "Alapkérdés",
                QuestionText = $"Hogyan működik a gyakorlatban a(z) {assessment.OrganizationName} szervezetnél a(z) '{reqDetail.Domain}' folyamat ({reqDetail.ReqCode}), és hogyan kezelik a dokumentumban ({assessment.DocumentTitle}) nem részletezett eseteket?",
                TargetRole = "IT Vezető / Információbiztonsági Felelős (IBF)",
                ExpectedEvidence = reqDetail.Controls.FirstOrDefault()?.PolicySuggested ?? "Jóváhagyott szabályzat és folyamatleírás"
            },
            new()
            {
                Category = "Mélyítő kérdés",
                QuestionText = $"Ki kezdeményezi és ki hagyja jóvá a folyamat kritikus lépéseit a(z) '{assessment.ItEnvironment}' környezetben, valamint mennyi időn belül kerülnek végrehajtásra a változtatások?",
                TargetRole = "Rendszergazda / HR Vezető",
                ExpectedEvidence = "Ticketing rendszer (pl. Jira/ServiceNow) munkafolyamat-bejegyzések"
            },
            new()
            {
                Category = "Bizonyítékot bekérő kérdés",
                QuestionText = $"Milyen konkrét, dátummal és aláírással/időbélyeggel ellátott bizonyíték igazolja, hogy a kontroll az elmúlt 12 hónapban ténylegesen működött? (Feltárt bizonytalanság: {assessment.AiResult.Uncertainties})",
                TargetRole = "IT Üzemeltetés / Rendszergazda",
                ExpectedEvidence = string.Join("; ", reqDetail.Controls.SelectMany(c => c.Evidences).Take(2))
            },
            new()
            {
                Category = "Ellenőrző kérdés",
                QuestionText = "Ha most szúrópróbaszerűen kiválasztunk 3 múlt havi eseményt (pl. kilépett dolgozót, rendszergazdai műveletet vagy mentési futást), összevethető-e a valós rendszernapló a szabályzatban előírt határidőkkel?",
                TargetRole = "Információbiztonsági Felelős (IBF) + Auditor",
                ExpectedEvidence = "Élő rendszerkonzol / Active Directory / Mentési napló helyszíni bemutatása"
            }
        };
    }

    public static async Task<string> GenerateFollowUpQuestionAsync(GeneratedQuestionDto question, string answer, string? userApiKey)
    {
        string apiKey = !string.IsNullOrWhiteSpace(userApiKey)
            ? userApiKey
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";

        string prompt = $"""
            Egy NIS2 kiberbiztonsági auditinterjún az auditor a következő kérdést tette fel:
            KÉRDÉS ({question.Category}): {question.QuestionText}
            ELVÁRT BIZONYÍTÉK: {question.ExpectedEvidence}
            AZ ÜGYFÉL VÁLASZA: {answer}

            Értékeld röviden (1 mondatban) a választ, majd fogalmazz meg 1 darab célzott követő (follow-up) vagy bizonyítékot bekérő kérdést az auditor számára! Válaszolj sima magyar szövegként (JSON nélkül).
            """;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                return await CallGeminiApiRawTextAsync(apiKey, prompt);
            }
            catch { }
        }

        return $"Értékelés: A megadott válasz (\"{answer}\") rögzítésre került, de önmagában a szóbeli nyilatkozat nem minősül auditbizonyítéknak. Követő kérdés: Kérjük, mutassa be a folyamat tényleges megtörténtét igazoló naplóbejegyzést vagy jegyzőkönyvet ({question.ExpectedEvidence}) az elmúlt negyedévből!";
    }

    public static async Task<RemediationPlanDto> GenerateRemediationPlanAsync(
        RequirementDetailDto reqDetail, SavedAssessmentDto assessment, string? userApiKey)
    {
        string apiKey = !string.IsNullOrWhiteSpace(userApiKey)
            ? userApiKey
            : Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";

        string prompt = $"""
            Te egy NIS2 kiberbiztonsági tanácsadó AI asszisztens vagy.
            A vizsgált dokumentum ('{assessment.DocumentTitle}') hiányosságokat mutat a(z) {reqDetail.ReqCode} ({reqDetail.Domain}) követelményhez képest.
            Készíts konkrét, azonnal beilleszthető szabályzat-kiegészítési szövegjavaslatot és 3 lépéses intézkedési tervet!

            SZERVEZET ÉS IT KÖRNYEZET: {assessment.OrganizationName} ({assessment.ItEnvironment})
            JOGSZABÁLYI KÖVETELMÉNY: {reqDetail.RegulationCode} - {reqDetail.Description}
            FELTÁRT HIÁNYOSSÁGOK: {assessment.AiResult.Uncertainties}

            Válaszolj kizárólag JSON objektumként a következő mezőkkel:
            - suggestedPolicyText (string: konkrét, hivatalos magyar nyelvű szabályzati szakasz tervezet)
            - actionSteps (string tömb: 3 konkrét végrehajtandó lépés)
            - responsibleRole (string: felelős szerepkör)
            - estimatedDeadline (string: javasolt végrehajtási határidő)
            """;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                string jsonResponse = await CallGeminiApiAsync(apiKey, prompt);
                var parsed = JsonSerializer.Deserialize<RemediationPlanDto>(jsonResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.SuggestedPolicyText))
                    return parsed;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gemini API intézkedési terv figyelmeztetés: {ex.Message}");
            }
        }

        var mandatoryCtrls = reqDetail.Controls.Where(c => c.IsMandatoryByLaw).ToList();
        return new RemediationPlanDto
        {
            SuggestedPolicyText = $"[TERVEZET – {reqDetail.ReqCode} KIEGÉSZÍTÉS] A(z) {assessment.OrganizationName} a(z) {reqDetail.RegulationCode} előírásainak megfelelően kötelezően előírja: {string.Join("; ", mandatoryCtrls.Select(c => $"{c.ControlName} ({c.ProcedureSuggested})"))}. A végrehajtást az érintett felelősök ({reqDetail.AffectedRoles}) dokumentáltan és visszakereshetően rögzítik a(z) {assessment.ItEnvironment} környezetben.",
            ActionSteps = new List<string>
            {
                $"1. A '{assessment.DocumentTitle}' dokumentum kiegészítése a fenti jogszabályi rendelkezésekkel és vezetői jóváhagyása.",
                $"2. A technikai kontrollok élesítése a(z) {assessment.ItEnvironment} környezetben ({string.Join(", ", mandatoryCtrls.Select(c => c.ControlName))}).",
                $"3. Auditbizonyítékok összegyűjtése és archiválása: {string.Join(", ", reqDetail.Controls.SelectMany(c => c.Evidences).Take(2))}."
            },
            ResponsibleRole = reqDetail.AffectedRoles,
            EstimatedDeadline = "30 napon belül (Audit előtti felkészülési határidő)"
        };
    }

    private static async Task<string> CallGeminiApiAsync(string apiKey, string prompt)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiModel}:generateContent?key={apiKey}";
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                temperature = 0.1,
                responseMimeType = "application/json"
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "{}";
    }

    private static async Task<string> CallGeminiApiRawTextAsync(string apiKey, string prompt)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiModel}:generateContent?key={apiKey}";
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.2 }
        };

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await Http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "";
    }

    private static string BuildKnowledgeBasePromptContext(RequirementDetailDto req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Jogszabályi forrás: {req.RegulationCode} - {req.RegulationTitle} ({req.Section})");
        sb.AppendLine($"Követelmény kód és terület: {req.ReqCode} - {req.Domain}");
        sb.AppendLine($"Követelmény leírása: {req.Description}");
        sb.AppendLine($"Érintett szerepkörök: {req.AffectedRoles}");
        sb.AppendLine("Kapcsolódó kontrollok és bizonyítékok:");
        foreach (var c in req.Controls)
        {
            string typeLabel = c.IsMandatoryByLaw ? "[KÖTELEZŐ JOGSZABÁLYI ELŐÍRÁS]" : "[AJÁNLOTT JÓ GYAKORLAT]";
            sb.AppendLine($"- {typeLabel} Kontroll: {c.ControlName} | Szabályzat: {c.PolicySuggested} | Eljárás: {c.ProcedureSuggested} | Felelős: {c.ResponsibleRole}");
            sb.AppendLine($"  Elvárt bizonyítékok: {string.Join(", ", c.Evidences)}");
        }
        return sb.ToString();
    }

    private static AiComplianceResult RunLocalDeterministicAnalysis(RequirementDetailDto reqDetail, AssessmentRequest req)
    {
        string text = req.DocumentContent;
        string lower = text.ToLowerInvariant();

        bool hasPasswordOrAccess = lower.Contains("jelszó") || lower.Contains("jogosultság") || lower.Contains("hozzáférés") || lower.Contains("belépés");
        bool hasYearlyReview = lower.Contains("évente") || lower.Contains("időszakos") || lower.Contains("felülvizsgálat");
        bool hasBackupTest = lower.Contains("visszaállítás") || lower.Contains("teszt");
        bool hasIncidentTime = lower.Contains("24 óra") || lower.Contains("72 óra") || lower.Contains("sztfh") || lower.Contains("csirt");

        string snippet = text.Length > 180 ? text.Substring(0, 180) + "..." : text;

        var mandatoryCtrls = reqDetail.Controls.Where(c => c.IsMandatoryByLaw).Select(c => $"{c.ControlName} ({c.PolicySuggested})");
        var bestPracticeCtrls = reqDetail.Controls.Where(c => !c.IsMandatoryByLaw).Select(c => $"{c.ControlName} ({c.ProcedureSuggested})");

        string status = (hasYearlyReview || hasBackupTest || hasIncidentTime) ? "Részben felel meg" : "Hiányosság";
        if (hasPasswordOrAccess && hasYearlyReview && lower.Contains("mfa")) status = "Megfelel";

        return new AiComplianceResult
        {
            AiClaim = $"A(z) '{req.DocumentTitle}' dokumentum tartalmaz alapvető rendelkezéseket a(z) {reqDetail.Domain} területre vonatkozóan, azonban a(z) {reqDetail.ReqCode} követelmény maradéktalan teljesüléséhez operatív működési bizonyítékok és pontosított eljárásrendek szükségesek.",
            AiJustification = $"A rendszer összevetette a feltöltött szöveget a(z) {reqDetail.RegulationCode} ({reqDetail.Section}) előírásaival. A szövegben azonosíthatók alapfogalmak, de nem minden kötelező kontrollhoz szerepel végrehajtási bizonyíték.",
            DocumentCitation = $"Forrás: '{req.DocumentTitle}', 1. szakasz / szövegrészlet: \"{snippet}\" — Figyelmeztetés: A szabályzat megléte önmagában nem bizonyítja a folyamat tényleges működését.",
            MandatoryByLawAnalysis = $"A jogszabály ({reqDetail.RegulationCode}) kötelezően előírja: {string.Join("; ", mandatoryCtrls)}. Kötelezően bekérendő bizonyítékok: {string.Join(", ", reqDetail.Controls.SelectMany(c => c.Evidences))}.",
            BestPracticeSuggestions = bestPracticeCtrls.Any()
                ? $"Az AI jó gyakorlatként javasolja (jogszabályon túli szakmai ajánlás): {string.Join("; ", bestPracticeCtrls)}."
                : "Az AI jó gyakorlatként javasolja a jogosultság- és mentés-ellenőrzések negyedéves automatizált riportálását és SIEM rendszerbe kötését.",
            ConfidenceScore = 86,
            Uncertainties = "A feltöltött dokumentum csak szabályzati szöveget tartalmaz; nem csatoltak aláírt felülvizsgálati jegyzőkönyvet, AD jogosultságlistát vagy tesztnaplót.",
            WhatExpertMustVerify = $"Az auditornak helyszíni interjún és mintavétellel kell ellenőriznie a felelősöknél ({reqDetail.AffectedRoles}), hogy a szabályzatban leírt folyamatok a(z) '{req.ItEnvironment}' környezetben ténylegesen megvalósulnak-e.",
            SuggestedStatus = status
        };
    }
}

// ============================================================================
// 7. JELENTÉS EXPORTÁLÓ SZOLGÁLTATÁS ("Tervezet" megjelöléssel)
// ============================================================================

public static class ReportExportService
{
    public static string? GenerateHtmlReport(string connStr, int assessmentId)
    {
        var a = AssessmentRepository.GetAssessmentById(connStr, assessmentId);
        if (a == null) return null;
        var questions = AssessmentRepository.GetQuestionsByAssessmentId(connStr, assessmentId);

        var qRows = new StringBuilder();
        foreach (var q in questions)
        {
            qRows.AppendLine($"""
                <tr>
                    <td><strong>{q.Category}</strong></td>
                    <td>{q.QuestionText}</td>
                    <td>{q.TargetRole}</td>
                    <td>{q.ExpectedEvidence}</td>
                    <td>{(string.IsNullOrWhiteSpace(q.AuditeeAnswer) ? "<em>Még nem rögzített</em>" : q.AuditeeAnswer)}</td>
                    <td>{(string.IsNullOrWhiteSpace(q.AiFollowUpQuestion) ? "-" : q.AiFollowUpQuestion)}</td>
                </tr>
                """);
        }

        return $$"""
            <!DOCTYPE html>
            <html lang='hu'>
            <head>
                <meta charset='UTF-8'>
                <title>NIS2 Audit-előkészítő Jelentés - #{{a.Id}}</title>
                <style>
                    body { font-family: Arial, sans-serif; margin: 35px; color: #1e293b; line-height: 1.5; }
                    .draft-banner { background: #fef3c7; border: 2px solid #d97706; color: #92400e; padding: 12px 18px; font-weight: bold; text-align: center; border-radius: 6px; margin-bottom: 24px; text-transform: uppercase; }
                    h1, h2, h3 { color: #0f172a; }
                    .box { border: 1px solid #cbd5e1; border-radius: 6px; padding: 16px; margin-bottom: 16px; background: #f8fafc; }
                    table { width: 100%; border-collapse: collapse; margin-top: 12px; }
                    th, td { border: 1px solid #cbd5e1; padding: 10px; text-align: left; vertical-align: top; font-size: 14px; }
                    th { background: #e2e8f0; }
                    .badge { display: inline-block; padding: 4px 10px; border-radius: 4px; font-weight: bold; background: #dbeafe; color: #1e40af; }
                    @media print { .no-print { display: none; } }
                </style>
            </head>
            <body>
                <div class='draft-banner'>
                    TERVEZET – SZAKÉRTŐI FELÜLVIZSGÁLAT SZÜKSÉGES (AI által támogatott előkészítő dokumentum)
                </div>
                <button class='no-print' onclick='window.print()' style='padding:8px 16px; cursor:pointer; margin-bottom:15px;'>Jelentés nyomtatása / Mentése PDF-ként</button>

                <h1>NIS2 Megfelelőségi és Audit-előkészítő Jelentés</h1>
                <p><strong>Szervezet:</strong> {{a.OrganizationName}} | <strong>IT környezet:</strong> {{a.ItEnvironment}} | <strong>Dátum:</strong> {{a.CreatedAt}}</p>
                <p><strong>Vizsgált dokumentum:</strong> {{a.DocumentTitle}}</p>
                <p><strong>Kapcsolódó NIS2 követelmény:</strong> {{a.ReqCode}} – {{a.Domain}} ({{a.RegulationRef}})</p>

                <h2>1. Fehér doboz (Explainable AI) Megfelelőségi Értékelés</h2>
                <div class='box'>
                    <p><strong>AI előzetes minősítési javaslat:</strong> <span class='badge'>{{a.AiResult.SuggestedStatus}}</span> (AI bizonyossági szint: <strong>{{a.AiResult.ConfidenceScore}}%</strong>)</p>
                    <p><strong>Szakértő (Auditor) végső döntése:</strong> <span class='badge'>{{a.ExpertFinalStatus}}</span></p>
                    <p><strong>Auditor megjegyzése:</strong> {{(string.IsNullOrWhiteSpace(a.ExpertNotes) ? "Nincs külön megjegyzés." : a.ExpertNotes)}}</p>
                    <hr/>
                    <p><strong>Mit állít az AI?</strong><br/>{{a.AiResult.AiClaim}}</p>
                    <p><strong>Mire alapozza (Indoklás)?</strong><br/>{{a.AiResult.AiJustification}}</p>
                    <p><strong>Mely dokumentumból származik (Pontos hivatkozás / Idézet)?</strong><br/>{{a.AiResult.DocumentCitation}}</p>
                    <p><strong>Ezt a jogszabály kötelezően előírja:</strong><br/>{{a.AiResult.MandatoryByLawAnalysis}}</p>
                    <p><strong>Ezt az AI javasolja jó gyakorlatként:</strong><br/>{{a.AiResult.BestPracticeSuggestions}}</p>
                    <p><strong>Fennálló bizonytalanságok / Hiányzó információk:</strong><br/>{{a.AiResult.Uncertainties}}</p>
                    <p><strong>Mit kell a humán szakértőnek ellenőriznie?</strong><br/>{{a.AiResult.WhatExpertMustVerify}}</p>
                </div>

                <h2>2. Generált Kategorizált Audit- és Ellenőrzési Kérdések</h2>
                <table>
                    <thead>
                        <tr>
                            <th>Kategória</th>
                            <th>Auditkérdés</th>
                            <th>Érintett szerepkör</th>
                            <th>Bekérendő bizonyíték</th>
                            <th>Interjúválasz</th>
                            <th>AI követő kérdés / Értékelés</th>
                        </tr>
                    </thead>
                    <tbody>
                        {{qRows}}
                    </tbody>
                </table>

                <p style='margin-top: 30px; font-size: 12px; color: #64748b;'>
                    Készült a NIS2-AuditGuard AI rendszerrel. A dokumentum minden AI által generált része tervezetnek minősül, a végső megfelelőségi minősítésért a kijelölt tanácsadó/auditor felel.
                </p>
            </body>
            </html>
            """;
    }
}

// ============================================================================
// 8. BEÉPÍTETT INTERAKTÍV FELHASZNÁLÓI FELÜLET (Nyers szövegblokk - """")
// ============================================================================

public static class WebUiTemplate
{
    public static string GetHtml() => """"
<!DOCTYPE html>
<html lang="hu">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>NIS2-AuditGuard AI | Tanácsadói és Audit-előkészítő Rendszer</title>
    <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet">
    <style>
        body { background-color: #f1f5f9; font-family: 'Segoe UI', system-ui, sans-serif; }
        .navbar-brand { font-weight: 700; letter-spacing: 0.5px; }
        .card { border: none; border-radius: 10px; box-shadow: 0 2px 10px rgba(0,0,0,0.06); }
        .whitebox-label { font-size: 0.78rem; font-weight: 700; text-transform: uppercase; color: #475569; letter-spacing: 0.04em; }
        .draft-tag { background: #fef3c7; color: #92400e; border: 1px solid #f59e0b; font-size: 0.8rem; padding: 3px 8px; border-radius: 4px; font-weight: 600; }
        .law-box { border-left: 4px solid #dc2626; background: #fef2f2; padding: 10px 14px; border-radius: 4px; }
        .practice-box { border-left: 4px solid #2563eb; background: #eff6ff; padding: 10px 14px; border-radius: 4px; }
        .login-overlay { position: fixed; inset: 0; background: rgba(15, 23, 42, 0.75); z-index: 9999; display: flex; align-items: center; justify-content: center; }
        .login-box { background: #fff; border-radius: 12px; max-width: 440px; width: 92%; padding: 28px; box-shadow: 0 15px 35px rgba(0,0,0,0.25); }
    </style>
</head>
<body>

<!-- BEJELENTKEZÉSI ABLAK (Ki és mikor jelentkezett be naplózásához) -->
<div id="loginModal" class="login-overlay">
    <div class="login-box">
        <h5 class="fw-bold mb-1">🔐 NIS2-AuditGuard AI – Bejelentkezés</h5>
        <p class="small text-muted mb-3">A rendszer minden bejelentkezést és szakértői műveletet személyhez és időbélyeghez kötve naplóz.</p>
        <div class="mb-3">
            <label class="form-label small fw-semibold">Felhasználó (Tanácsadó / Auditor) neve:</label>
            <input type="text" id="loginName" class="form-control" value="Kovács János">
        </div>
        <div class="mb-3">
            <label class="form-label small fw-semibold">Szerepkör:</label>
            <select id="loginRole" class="form-select">
                <option value="Vezető NIS2 Auditor">Vezető NIS2 Auditor</option>
                <option value="Kiberbiztonsági Tanácsadó">Kiberbiztonsági Tanácsadó</option>
                <option value="Információbiztonsági Felelős (IBF)">Információbiztonsági Felelős (IBF)</option>
            </select>
        </div>
        <button class="btn btn-primary w-100 fw-semibold" onclick="performLogin()">
            Bejelentkezés és Munkamenet Naplózása
        </button>
    </div>
</div>

<nav class="navbar navbar-dark bg-dark mb-4">
    <div class="container-fluid px-4">
        <span class="navbar-brand">🛡️ NIS2-AuditGuard AI – Tanácsadói & Audit-előkészítő Rendszer</span>
        <div class="d-flex align-items-center gap-2 flex-wrap">
            <span class="badge bg-success py-2 px-3" id="currentUserBadge">👤 Nincs bejelentkezve</span>
            <button class="btn btn-outline-light btn-sm" onclick="performLogout()">Felhasználó váltása / Kijelentkezés</button>
            <input type="password" id="geminiKey" class="form-control form-control-sm" style="width:220px;" placeholder="Gemini API kulcs (opcionális)">
        </div>
    </div>
</nav>

<div class="container-fluid px-4 pb-5">
    <div class="row g-4">
        <div class="col-lg-5">
            <div class="card p-4 mb-4">
                <h5 class="mb-3">1. NIS2 Tudásbázis & Követelmény Kiválasztása</h5>
                <div class="mb-3">
                    <label class="form-label fw-semibold">NIS2 Követelmény (Jogszabály → Követelmény → Kontroll):</label>
                    <select id="reqSelect" class="form-select" onchange="renderSelectedRequirement()"></select>
                </div>
                <div id="kbDetails" class="small bg-light p-3 rounded border mb-3"></div>

                <h5 class="mb-3 mt-2">2. Vizsgált Szervezet és Szabályzat Elemzése</h5>
                <div class="row g-2 mb-2">
                    <div class="col-md-6">
                        <label class="form-label small fw-semibold">Szervezet neve:</label>
                        <input type="text" id="orgName" class="form-control form-control-sm" value="Példa Logisztikai Zrt.">
                    </div>
                    <div class="col-md-6">
                        <label class="form-label small fw-semibold">IT környezet:</label>
                        <input type="text" id="itEnv" class="form-control form-control-sm" value="Hibrid AD + Azure + SAP ERP">
                    </div>
                </div>
                <div class="mb-2">
                    <label class="form-label small fw-semibold">Feltöltött / vizsgált dokumentum címe:</label>
                    <input type="text" id="docTitle" class="form-control form-control-sm" value="Hozzáférés-kezelési Szabályzat v2.1 (4. oldal)">
                </div>
                <div class="mb-2">
                    <div class="d-flex justify-content-between align-items-center mb-1">
                        <label class="form-label small fw-semibold mb-0">Dokumentum szövege (vagy .txt/.md fájl beolvasása):</label>
                        <input type="file" id="fileInput" class="form-control form-control-sm w-50" accept=".txt,.md,.csv,.json" onchange="loadFile(event)">
                    </div>
                    <textarea id="docContent" class="form-control form-control-sm" rows="6">4. SZAKASZ - JOGOSULTSÁGOK ÉS JELSZÓKEZELÉS (4. oldal)
4.1. Minden új belépő munkavállaló részére a közvetlen felettes e-mailben igényli meg a hozzáféréseket az IT üzemeltetéstől.
4.2. A felhasználói jelszavaknak legalább 10 karakter hosszúnak kell lenniük, és 90 naponta kötelező a jelszócsere.
4.3. Kilépéskor a HR osztály értesíti az IT-t a fiókok zárolásáról.
(Megjegyzés: A dokumentum nem tartalmaz rendelkezést az évenkénti jogosultság-felülvizsgálatra és a rendszergazdai MFA-ra.)</textarea>
                </div>
                <button class="btn btn-primary w-100 fw-semibold mt-2" onclick="runComplianceAssessment()" id="btnAssess">
                    🔍 1. AI-Funkció: NIS2 Megfelelőség-vizsgálat Indítása
                </button>
            </div>

            <!-- EREDMÉNY- ÉS ESEMÉNYNAPLÓ LEGÖRDÜLŐ MENÜVEL -->
            <div class="card p-4">
                <div class="d-flex justify-content-between align-items-center mb-2 flex-wrap gap-1">
                    <h6 class="mb-0 fw-bold">📜 Eredmény- és Eseménynapló (Audit Log)</h6>
                    <div class="d-flex gap-1 align-items-center">
                        <select id="logExportFormat" class="form-select form-select-sm" style="width: auto;" title="Letöltési formátum kiválasztása">
                            <option value="pdf">PDF (Nyomtatható)</option>
                            <option value="csv">CSV (Excel)</option>
                            <option value="txt">TXT (.log)</option>
                        </select>
                        <button class="btn btn-success btn-sm" onclick="downloadSelectedLog()" title="Kiválasztott formátum letöltése">📥 Letöltés</button>
                        <button class="btn btn-outline-secondary btn-sm" onclick="loadLogs()">🔄</button>
                    </div>
                </div>
                <div class="text-muted" style="font-size: 0.75rem; margin-bottom: 6px;">
                    Rögzíti, hogy ki és mikor jelentkezett be, valamint milyen elemzési és jóváhagyási műveletet végzett.
                </div>
                <div id="logContainer" style="max-height: 250px; overflow-y: auto;" class="small"></div>
            </div>
        </div>

        <div class="col-lg-7">
            <div id="resultPlaceholder" class="card p-5 text-center text-muted">
                <h5>Még nincs kiválasztott elemzés</h5>
                <p class="mb-0">Indíts egy új NIS2 megfelelőség-vizsgálatot a bal oldali űrlapon!</p>
            </div>

            <div id="resultPanel" class="d-none">
                <div class="card p-4 mb-4">
                    <div class="d-flex justify-content-between align-items-center mb-3">
                        <div>
                            <span class="draft-tag">TERVEZET – SZAKÉRTŐI FELÜLVIZSGÁLAT SZÜKSÉGES</span>
                            <h5 class="mt-2 mb-0" id="resHeader">Fehér doboz (Explainable AI) Értékelés</h5>
                        </div>
                        <a id="btnExport" href="#" target="_blank" class="btn btn-success btn-sm fw-semibold">
                            📄 Jelentés Exportálása (HTML / PDF)
                        </a>
                    </div>

                    <div class="row g-3 mb-3">
                        <div class="col-md-6">
                            <div class="p-3 bg-light rounded border">
                                <div class="whitebox-label">Mit állít az AI? (Előzetes javaslat & Bizonyosság)</div>
                                <div class="mt-1 fw-bold text-primary" id="resStatusAndConf"></div>
                                <div class="small mt-1" id="resClaim"></div>
                            </div>
                        </div>
                        <div class="col-md-6">
                            <div class="p-3 bg-light rounded border">
                                <div class="whitebox-label">Mely dokumentumból és követelményből származik?</div>
                                <div class="small fw-semibold text-dark mt-1" id="resReqRef"></div>
                                <div class="small text-secondary mt-1 fst-italic" id="resCitation"></div>
                            </div>
                        </div>
                    </div>

                    <div class="mb-3">
                        <div class="whitebox-label mb-1">Mire alapozza az AI? (Szakmai indoklás)</div>
                        <div class="small bg-white p-2 rounded border" id="resJustification"></div>
                    </div>

                    <div class="row g-3 mb-3">
                        <div class="col-md-6">
                            <div class="law-box small">
                                <strong>⚖️ Ezt a követelmény / jogszabály kötelezően előírja:</strong>
                                <div id="resMandatory" class="mt-1"></div>
                            </div>
                        </div>
                        <div class="col-md-6">
                            <div class="practice-box small">
                                <strong>💡 Ezt az AI javasolja jó gyakorlatként:</strong>
                                <div id="resBestPractice" class="mt-1"></div>
                            </div>
                        </div>
                    </div>

                    <div class="row g-3 mb-3">
                        <div class="col-md-6">
                            <div class="p-2 bg-warning-subtle border border-warning rounded small">
                                <strong>⚠️ Bizonytalanságok / Hiányzó információk:</strong>
                                <div id="resUncertainties" class="mt-1"></div>
                            </div>
                        </div>
                        <div class="col-md-6">
                            <div class="p-2 bg-info-subtle border border-info rounded small">
                                <strong>🔎 Mit kell a humán szakértőnek ellenőriznie?</strong>
                                <div id="resVerify" class="mt-1"></div>
                            </div>
                        </div>
                    </div>

                    <div class="border-top pt-3 mt-2">
                        <h6 class="fw-bold mb-2">👨‍⚖️ Tanácsadói / Auditori Végső Döntés (Az AI nem dönt önállóan)</h6>
                        <div class="row g-2 align-items-center">
                            <div class="col-md-4">
                                <select id="expertStatus" class="form-select form-select-sm">
                                    <option value="Megfelel (Szakértő által jóváhagyva)">Megfelel (Jóváhagyva)</option>
                                    <option value="Részben felel meg (Intézkedés szükséges)" selected>Részben felel meg</option>
                                    <option value="Kritikus megfelelőségi hiányosság">Kritikus hiányosság</option>
                                </select>
                            </div>
                            <div class="col-md-5">
                                <input type="text" id="expertNotes" class="form-control form-control-sm" placeholder="Auditor szakmai megjegyzése...">
                            </div>
                            <div class="col-md-3">
                                <button class="btn btn-dark btn-sm w-100" onclick="saveExpertDecision()">Döntés Rögzítése</button>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="card p-4 mb-4">
                    <div class="d-flex justify-content-between align-items-center mb-3">
                        <div>
                            <h5 class="mb-0">🎤 2. AI-Funkció: Kategorizált Audit- és Ellenőrzési Kérdések</h5>
                            <small class="text-muted">Alapkérdés, Mélyítő kérdés, Bizonyítékot bekérő kérdés és Ellenőrző kérdés</small>
                        </div>
                        <button class="btn btn-warning fw-semibold btn-sm" onclick="generateQuestions()" id="btnGenQ">
                            ⚡ Auditkérdések Generálása AI-val
                        </button>
                    </div>
                    <div id="questionsContainer"></div>
                </div>

                <div class="card p-4">
                    <div class="d-flex justify-content-between align-items-center mb-3">
                        <div>
                            <h5 class="mb-0">🛠️ 3. AI-Funkció: Szabályzat-kiegészítés & Intézkedési Terv</h5>
                            <small class="text-muted">Automatikus szövegtervezet és akcióterv a feltárt hiányosságok megszüntetésére</small>
                        </div>
                        <button class="btn btn-info text-white fw-semibold btn-sm" onclick="generateRemediation()" id="btnGenRem">
                            ✨ Javaslat Generálása AI-val
                        </button>
                    </div>
                    <div id="remediationContainer">
                        <div class="text-muted small">Kattints a 'Javaslat Generálása AI-val' gombra a szabályzat-kiegészítési tervezet és az intézkedési terv elkészítéséhez!</div>
                    </div>
                </div>
            </div>
        </div>
    </div>
</div>

<script>
let knowledgeBase = [];
let currentAssessmentId = null;
let loggedInAuditor = "";

async function init() {
    const res = await fetch('/api/knowledgebase');
    knowledgeBase = await res.json();
    const sel = document.getElementById('reqSelect');
    sel.innerHTML = knowledgeBase.map(r => `<option value="${r.id}">${r.reqCode} – ${r.domain}</option>`).join('');
    renderSelectedRequirement();
    loadLogs();
}

async function performLogin() {
    const nameInput = document.getElementById('loginName').value.trim();
    const roleInput = document.getElementById('loginRole').value;
    if (!nameInput) {
        alert('Kérlek, add meg a bejelentkező felhasználó nevét!');
        return;
    }

    const res = await fetch('/api/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userName: nameInput, role: roleInput })
    });
    const data = await res.json();
    loggedInAuditor = data.actor;
    document.getElementById('currentUserBadge').innerText = `👤 Bejelentkezve: ${loggedInAuditor} (${data.timestamp})`;
    document.getElementById('loginModal').classList.add('d-none');
    loadLogs();
}

async function performLogout() {
    if (loggedInAuditor) {
        await fetch('/api/logout', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ userName: loggedInAuditor, role: '' })
        });
    }
    loggedInAuditor = "";
    document.getElementById('currentUserBadge').innerText = "👤 Nincs bejelentkezve";
    document.getElementById('loginModal').classList.remove('d-none');
    loadLogs();
}

function downloadSelectedLog() {
    const format = document.getElementById('logExportFormat').value;
    downloadLog(format);
}

function downloadLog(format) {
    const userParam = encodeURIComponent(loggedInAuditor || 'Auditor');
    const url = `/api/logs/download/${format}?user=${userParam}`;
    if (format === 'pdf') {
        window.open(url, '_blank');
    } else {
        window.location.href = url;
    }
    setTimeout(loadLogs, 800);
}

function renderSelectedRequirement() {
    const id = parseInt(document.getElementById('reqSelect').value);
    const req = knowledgeBase.find(x => x.id === id);
    if (!req) return;
    const ctrls = req.controls.map(c =>
        `<li><strong>${c.controlName}</strong> ${c.isMandatoryByLaw ? '<span class="badge bg-danger">Jogszabályi</span>' : '<span class="badge bg-primary">Jó gyakorlat</span>'}<br/>
         <em>Szabályzat:</em> ${c.policySuggested} | <em>Bizonyíték:</em> ${c.evidences.join(', ')}</li>`
    ).join('');
    document.getElementById('kbDetails').innerHTML = `
        <div><strong>Jogszabályi forrás:</strong> ${req.regulationCode} (${req.section})</div>
        <div class="mt-1"><strong>Követelmény:</strong> ${req.description}</div>
        <div class="mt-1"><strong>Érintett szerepkörök:</strong> ${req.affectedRoles}</div>
        <div class="mt-2"><strong>Tudásbázis kontrollok:</strong><ul class="mb-0 ps-3">${ctrls}</ul></div>
    `;
}

function loadFile(event) {
    const file = event.target.files[0];
    if (!file) return;
    document.getElementById('docTitle').value = file.name;
    const reader = new FileReader();
    reader.onload = e => document.getElementById('docContent').value = e.target.result;
    reader.readAsText(file);
}

async function runComplianceAssessment() {
    const btn = document.getElementById('btnAssess');
    btn.disabled = true;
    btn.innerText = '⏳ AI elemzés folyamatban...';

    const payload = {
        requirementId: parseInt(document.getElementById('reqSelect').value),
        organizationName: document.getElementById('orgName').value,
        itEnvironment: document.getElementById('itEnv').value,
        documentTitle: document.getElementById('docTitle').value,
        documentContent: document.getElementById('docContent').value,
        apiKey: document.getElementById('geminiKey').value,
        auditorName: loggedInAuditor
    };

    const res = await fetch('/api/assess', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });
    const data = await res.json();
    btn.disabled = false;
    btn.innerText = '🔍 1. AI-Funkció: NIS2 Megfelelőség-vizsgálat Indítása';

    if (res.ok) {
        currentAssessmentId = data.assessmentId;
        displayAssessment(data.result, data.requirement);
        loadLogs();
    } else {
        alert(data.error || 'Hiba történt az elemzés során.');
    }
}

function displayAssessment(ai, req) {
    document.getElementById('resultPlaceholder').classList.add('d-none');
    document.getElementById('resultPanel').classList.remove('d-none');
    const userParam = encodeURIComponent(loggedInAuditor || 'Auditor');
    document.getElementById('btnExport').href = `/api/export/${currentAssessmentId}?user=${userParam}`;
    document.getElementById('resStatusAndConf').innerText = `${ai.suggestedStatus} (AI Konfidencia: ${ai.confidenceScore}%)`;
    document.getElementById('resClaim').innerText = ai.aiClaim;
    document.getElementById('resReqRef').innerText = `${req.reqCode} (${req.regulationCode}, ${req.section})`;
    document.getElementById('resCitation').innerText = ai.documentCitation;
    document.getElementById('resJustification').innerText = ai.aiJustification;
    document.getElementById('resMandatory').innerText = ai.mandatoryByLawAnalysis;
    document.getElementById('resBestPractice').innerText = ai.bestPracticeSuggestions;
    document.getElementById('resUncertainties').innerText = ai.uncertainties;
    document.getElementById('resVerify').innerText = ai.whatExpertMustVerify;
    document.getElementById('questionsContainer').innerHTML = `<div class="text-muted small">Kattints az 'Auditkérdések Generálása AI-val' gombra a célzott kérdéssor előállításához!</div>`;
    document.getElementById('remediationContainer').innerHTML = `<div class="text-muted small">Kattints a 'Javaslat Generálása AI-val' gombra a szabályzat-kiegészítési tervezet és az intézkedési terv elkészítéséhez!</div>`;
}

async function saveExpertDecision() {
    if (!currentAssessmentId) return;
    const payload = {
        expertFinalStatus: document.getElementById('expertStatus').value,
        expertNotes: document.getElementById('expertNotes').value,
        auditorName: loggedInAuditor
    };
    await fetch(`/api/assessments/${currentAssessmentId}/verify`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });
    loadLogs();
    alert('Szakértői döntés rögzítve és naplózva!');
}

async function generateQuestions() {
    if (!currentAssessmentId) return;
    const btn = document.getElementById('btnGenQ');
    btn.disabled = true;
    btn.innerText = '⏳ Kérdések generálása...';

    const res = await fetch(`/api/generate-questions/${currentAssessmentId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            apiKey: document.getElementById('geminiKey').value,
            auditorName: loggedInAuditor
        })
    });
    const questions = await res.json();
    btn.disabled = false;
    btn.innerText = '⚡ Auditkérdések Generálása AI-val';
    renderQuestions(questions);
    loadLogs();
}

async function generateRemediation() {
    if (!currentAssessmentId) return;
    const btn = document.getElementById('btnGenRem');
    btn.disabled = true;
    btn.innerText = '⏳ Javaslat generálása...';

    const res = await fetch(`/api/generate-remediation/${currentAssessmentId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            apiKey: document.getElementById('geminiKey').value,
            auditorName: loggedInAuditor
        })
    });
    const rem = await res.json();
    btn.disabled = false;
    btn.innerText = '✨ Javaslat Generálása AI-val';

    const stepsHtml = (rem.actionSteps || []).map(s => `<li>${s}</li>`).join('');
    document.getElementById('remediationContainer').innerHTML = `
        <div class="p-3 bg-light rounded border small">
            <div class="whitebox-label mb-1">📝 Javasolt szabályzat-kiegészítési szövegtervezet:</div>
            <div class="p-2 bg-white border rounded fst-italic mb-2">${rem.suggestedPolicyText}</div>
            <div class="whitebox-label mb-1">📋 Intézkedési lépések:</div>
            <ul class="mb-2 ps-3">${stepsHtml}</ul>
            <div class="d-flex justify-content-between text-secondary">
                <span>👤 <strong>Felelős:</strong> ${rem.responsibleRole}</span>
                <span>⏱️ <strong>Javasolt határidő:</strong> ${rem.estimatedDeadline}</span>
            </div>
        </div>
    `;
    loadLogs();
}

function renderQuestions(questions) {
    const container = document.getElementById('questionsContainer');
    container.innerHTML = questions.map(q => `
        <div class="border rounded p-3 mb-2 bg-light">
            <div class="d-flex justify-content-between">
                <span class="badge bg-dark">${q.category}</span>
                <span class="small text-secondary">👤 Címzett: <strong>${q.targetRole}</strong></span>
            </div>
            <div class="fw-semibold mt-2">${q.questionText}</div>
            <div class="small text-muted mt-1">📂 Bekérendő bizonyíték: ${q.expectedEvidence}</div>
            <div class="input-group input-group-sm mt-2">
                <input type="text" id="ans_${q.id}" class="form-control" placeholder="Írd be az interjúalany válaszát a követő kérdés generálásához..." value="${q.auditeeAnswer || ''}">
                <button class="btn btn-outline-primary" onclick="submitInterviewAnswer(${q.id})">AI Válaszértékelés & Követő kérdés</button>
            </div>
            <div id="fup_${q.id}" class="small text-primary fw-semibold mt-2 ${q.aiFollowUpQuestion ? '' : 'd-none'}">
                🤖 ${q.aiFollowUpQuestion}
            </div>
        </div>
    `).join('');
}

async function submitInterviewAnswer(qId) {
    const ans = document.getElementById(`ans_${qId}`).value;
    const res = await fetch(`/api/questions/${qId}/answer`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            auditeeAnswer: ans,
            apiKey: document.getElementById('geminiKey').value,
            auditorName: loggedInAuditor
        })
    });
    const data = await res.json();
    const fupEl = document.getElementById(`fup_${qId}`);
    fupEl.classList.remove('d-none');
    fupEl.innerText = '🤖 ' + data.aiFollowUpQuestion;
    loadLogs();
}

async function loadLogs() {
    const res = await fetch('/api/logs');
    const logs = await res.json();
    document.getElementById('logContainer').innerHTML = logs.map(l => {
        let badgeClass = 'bg-secondary';
        if (l.eventType === 'USER_LOGIN') badgeClass = 'bg-success';
        else if (l.eventType === 'USER_LOGOUT') badgeClass = 'bg-warning text-dark';
        else if (l.actor.startsWith('AI_AGENT')) badgeClass = 'bg-primary';
        return `
        <div class="border-bottom py-1">
            <span class="text-muted">[${l.timestamp}]</span>
            <span class="badge ${badgeClass}">${l.actor}</span>
            <strong>${l.eventType}:</strong> ${l.summary}
        </div>`;
    }).join('');
}

init();
</script>
</body>
</html>
"""";
}