using EMR.Domain.Entities;
using EMR.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Tesseract;

namespace EMR.API.Controllers;

[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly string _uploadRootDirectory;
    private readonly IConfiguration? _configuration;

    public DocumentsController(AppDbContext context, IConfiguration? configuration = null)
    {
        _context = context;
        _configuration = configuration;
        _uploadRootDirectory = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "medical_records");
        if (!Directory.Exists(_uploadRootDirectory))
        {
            Directory.CreateDirectory(_uploadRootDirectory);
        }
    }

    private async Task<string> GenerateClinicalSummaryWithGroqAsync(string prompt, string fallbackText)
    {
        try
        {
            string apiKey = _configuration?["GroqSettings:ApiKey"] ?? Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Equals("YOUR_GROQ_API_KEY_HERE", StringComparison.OrdinalIgnoreCase))
            {
                return fallbackText;
            }

            string endpoint = "https://api.groq.com/openai/v1/chat/completions";
            string model = _configuration?["GroqSettings:Model"] ?? "llama-3.3-70b-versatile";

            // Smart Auto-Routing: If key starts with "xai-" or model contains "grok", switch to xAI (Grok) API automatically
            if (apiKey.Trim().StartsWith("xai-", StringComparison.OrdinalIgnoreCase) || model.Contains("grok", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = "https://api.x.ai/v1/chat/completions";
                if (!model.Contains("grok", StringComparison.OrdinalIgnoreCase))
                {
                    model = "grok-beta"; // Default xAI model when an xai- key is used
                }
            }

            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            var requestBody = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = "You are an expert Senior Clinical Physician. Generate concise, highly accurate, professional medical summaries based strictly on the provided clinical records or OCR text. Do not hallucinate or invent any information." },
                    new { role = "user", content = prompt }
                },
                temperature = 0.2,
                max_tokens = 350
            };

            var jsonContent = new System.Net.Http.StringContent(
                System.Text.Json.JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(endpoint, jsonContent);
            if (response.IsSuccessStatusCode)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(responseString);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    return content.Trim();
                }
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[AI API Error] Endpoint: {endpoint}, Status: {response.StatusCode}, Response: {err}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI API Warning] Failed to generate AI summary: {ex.Message}. Falling back to rule-based summary.");
        }

        return fallbackText;
    }

    [HttpGet("{patientId}/records")]
    public async Task<IActionResult> GetPatientRecords(int patientId)
    {
        await EnsureDatabaseSchemaUpdatedAsync();
        var documents = await _context.PatientDocuments
            .Where(d => d.PatientId == patientId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        var medications = await _context.PatientMedications
            .Where(m => m.PatientId == patientId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var labFindings = await _context.PatientLabFindings
            .Where(l => l.PatientId == patientId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

        var radiologyNotes = await _context.PatientRadiologyNotes
            .Where(r => r.PatientId == patientId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        // Build web-accessible fileUrl for each document
        var documentDtos = documents.Select(d =>
        {
            string? fileUrl = null;
            if (!string.IsNullOrEmpty(d.FilePath))
            {
                var wwwrootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                if (d.FilePath.StartsWith(wwwrootPath, StringComparison.OrdinalIgnoreCase))
                {
                    var relativePath = d.FilePath.Substring(wwwrootPath.Length).Replace("\\", "/");
                    fileUrl = relativePath;
                }
            }

            return new
            {
                d.Id,
                d.Category,
                d.FileName,
                d.FilePath,
                fileUrl,
                rawTextSummary = d.RawTextSummary,
                uploadedAt = d.UploadedAt
            };
        }).ToList();

        return Ok(new
        {
            documents = documentDtos,
            medications,
            labFindings,
            radiologyNotes
        });
    }

    [HttpGet("{patientId}/clinical-summary")]
    public async Task<IActionResult> GetPatientClinicalSummary(int patientId)
    {
        await EnsureDatabaseSchemaUpdatedAsync();

        var patient = await _context.Patients.FirstOrDefaultAsync(p => p.PatientId == patientId);
        string patientName = patient != null ? patient.FullName : $"Patient #{patientId}";

        var documents = await _context.PatientDocuments
            .Where(d => d.PatientId == patientId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        var medications = await _context.PatientMedications
            .Where(m => m.PatientId == patientId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var labFindings = await _context.PatientLabFindings
            .Where(l => l.PatientId == patientId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

        var radiologyNotes = await _context.PatientRadiologyNotes
            .Where(r => r.PatientId == patientId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        // Dynamically synthesize a real, highly accurate Executive Overview from actual DB records
        string executiveOverview;
        string? prescriptionSummary = null;
        string? labReportSummary = null;
        string? radiologySummary = null;

        int totalRecords = documents.Count + medications.Count + labFindings.Count + radiologyNotes.Count;

        if (totalRecords == 0)
        {
            executiveOverview = "No clinical documents, prescriptions, or scan reports uploaded yet. Upload documents via OCR to auto-generate clinical summary.";
        }
        else
        {
            var overviewParts = new List<string>();
            overviewParts.Add($"Patient has {documents.Count} scanned document(s) and {medications.Count + labFindings.Count + radiologyNotes.Count} extracted clinical record(s) on file.");

            if (medications.Count > 0)
            {
                var topMeds = medications.Select(m => m.MedicineName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().Take(3);
                overviewParts.Add($"Currently maintained on {medications.Count} active medication(s) (including {string.Join(", ", topMeds)}).");
            }

            if (radiologyNotes.Count > 0)
            {
                var latestRad = radiologyNotes.First();
                string imp = latestRad.ImpressionText;
                if (imp.Length > 110) imp = imp.Substring(0, 110) + "...";
                overviewParts.Add($"Latest Radiology/Scan finding: \"{imp}\".");
            }

            if (labFindings.Count > 0)
            {
                var abnormalLabs = labFindings.Where(l => l.IsAbnormal || l.Status.Contains("High", StringComparison.OrdinalIgnoreCase) || l.Status.Contains("Low", StringComparison.OrdinalIgnoreCase) || l.Status.Contains("Positive", StringComparison.OrdinalIgnoreCase)).ToList();
                if (abnormalLabs.Any())
                {
                    var testList = abnormalLabs.Select(l => $"{l.TestName} ({l.ObservedValue} {l.Unit})").Take(2);
                    overviewParts.Add($"Critical/Abnormal Lab markers: {string.Join(", ", testList)}.");
                }
                else
                {
                    overviewParts.Add($"{labFindings.Count} lab parameter(s) monitored within normal limits.");
                }
            }

            executiveOverview = string.Join(" ", overviewParts);

            // ⚡ Groq AI Executive Clinical Snapshot Synthesizer (Zero impact on auto-fill)
            var allDocsSummary = string.Join("\n- ", documents.Take(10).Select(d => $"[{d.Category} - {d.FileName}]: {d.RawTextSummary}"));
            var allMedsSummary = string.Join(", ", medications.Take(15).Select(m => $"{m.MedicineName} {m.Dosage} ({m.Frequency})"));
            var allLabsSummary = string.Join(", ", labFindings.Take(15).Select(l => $"{l.TestName}: {l.ObservedValue} {l.Unit} ({l.Status})"));
            var allRadsSummary = string.Join(" | ", radiologyNotes.Take(5).Select(r => r.ImpressionText));

            string groqPrompt = $"Patient Name: {patientName}\n" +
                                $"Scanned Document Summaries:\n- {allDocsSummary}\n\n" +
                                $"Active Medications: {allMedsSummary}\n" +
                                $"Lab Markers: {allLabsSummary}\n" +
                                $"Radiology Findings: {allRadsSummary}\n\n" +
                                $"Task: Write a concise, 100% accurate, professional 3-sentence Executive Clinical Snapshot for the attending physician summarizing this patient's primary medical profile, current medication regimen, and notable lab/scan findings based strictly on the above records.";

            executiveOverview = await GenerateClinicalSummaryWithGroqAsync(groqPrompt, fallbackText: executiveOverview);

            // ⚡ Category-Wise Summaries Synthesis
            var rxDocs = documents.Where(d => d.Category.Equals("Prescription", StringComparison.OrdinalIgnoreCase)).ToList();
            var labDocs = documents.Where(d => d.Category.Equals("LabReport", StringComparison.OrdinalIgnoreCase)).ToList();
            var radDocs = documents.Where(d => d.Category.Equals("Radiology", StringComparison.OrdinalIgnoreCase)).ToList();

            if (medications.Any() || rxDocs.Any())
            {
                var medNames = medications.Select(m => $"{m.MedicineName} {m.Dosage}".Trim()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
                string rxSummaryText = medNames.Any()
                    ? $"Patient is maintained on {medNames.Count} active medication(s) across {rxDocs.Count} uploaded prescription(s) (including {string.Join(", ", medNames.Take(4))})."
                    : $"Analyzed {rxDocs.Count} prescription document(s).";

                if (medNames.Any())
                {
                    string prompt = $"Active Medications: {string.Join(", ", medications.Take(12).Select(m => $"{m.MedicineName} {m.Dosage} ({m.Frequency})"))}\nUploaded Prescription Notes: {string.Join(" | ", rxDocs.Take(3).Select(d => d.RawTextSummary))}\n\nTask: Write a concise, professional 2-sentence Prescription Summary for the attending physician summarizing the patient's current active medication regimen.";
                    prescriptionSummary = await GenerateClinicalSummaryWithGroqAsync(prompt, fallbackText: rxSummaryText);
                }
                else
                {
                    prescriptionSummary = rxSummaryText;
                }
            }

            if (labFindings.Any() || labDocs.Any())
            {
                var abnormalLabs = labFindings.Where(l => l.IsAbnormal || l.Status.Contains("High", StringComparison.OrdinalIgnoreCase) || l.Status.Contains("Low", StringComparison.OrdinalIgnoreCase)).ToList();
                string labSummaryText = abnormalLabs.Any()
                    ? $"Recent blood panel highlights {abnormalLabs.Count} critical/abnormal marker(s) ({string.Join(", ", abnormalLabs.Take(3).Select(l => $"{l.TestName}: {l.ObservedValue} {l.Unit}"))}) across {labDocs.Count} report(s)."
                    : $"{labFindings.Count} lab parameter(s) monitored across {labDocs.Count} report(s) within normal limits.";

                if (labFindings.Any())
                {
                    string prompt = $"Lab Markers: {string.Join(", ", labFindings.Take(15).Select(l => $"{l.TestName}: {l.ObservedValue} {l.Unit} ({l.Status})"))}\nUploaded Lab Report Notes: {string.Join(" | ", labDocs.Take(3).Select(d => d.RawTextSummary))}\n\nTask: Write a concise, professional 2-sentence Lab Report Summary for the attending physician highlighting any abnormal glucose, renal, or hematological parameters.";
                    labReportSummary = await GenerateClinicalSummaryWithGroqAsync(prompt, fallbackText: labSummaryText);
                }
                else
                {
                    labReportSummary = labSummaryText;
                }
            }

            if (radiologyNotes.Any() || radDocs.Any())
            {
                var latestRad = radiologyNotes.FirstOrDefault();
                string radSummaryText = latestRad != null
                    ? $"Latest Scan/Imaging impression: \"{latestRad.ImpressionText}\""
                    : $"Analyzed {radDocs.Count} radiology/scan report(s).";

                if (radiologyNotes.Any())
                {
                    string prompt = $"Radiology & Scan Findings: {string.Join(" | ", radiologyNotes.Take(5).Select(r => r.ImpressionText))}\nUploaded Scan Notes: {string.Join(" | ", radDocs.Take(3).Select(d => d.RawTextSummary))}\n\nTask: Write a concise, professional 2-sentence Radiology / Scan Summary for the attending physician summarizing the imaging impressions without speculation.";
                    radiologySummary = await GenerateClinicalSummaryWithGroqAsync(prompt, fallbackText: radSummaryText);
                }
                else
                {
                    radiologySummary = radSummaryText;
                }
            }
        }

        DateTime? lastUpdated = null;
        if (documents.Any()) lastUpdated = documents.Max(d => d.UploadedAt);
        if (medications.Any())
        {
            var maxMed = medications.Max(m => m.CreatedAt);
            if (!lastUpdated.HasValue || maxMed > lastUpdated.Value) lastUpdated = maxMed;
        }
        if (radiologyNotes.Any())
        {
            var maxRad = radiologyNotes.Max(r => r.CreatedAt);
            if (!lastUpdated.HasValue || maxRad > lastUpdated.Value) lastUpdated = maxRad;
        }
        if (labFindings.Any())
        {
            var maxLab = labFindings.Max(l => l.CreatedAt);
            if (!lastUpdated.HasValue || maxLab > lastUpdated.Value) lastUpdated = maxLab;
        }

        return Ok(new
        {
            patientId,
            patientName,
            totalDocumentsScanned = documents.Count,
            lastUpdated = lastUpdated ?? DateTime.UtcNow,
            executiveOverview,
            prescriptionSummary,
            labReportSummary,
            radiologySummary,
            activeMedications = medications.Take(15).ToList(),
            radiologyHighlights = radiologyNotes.Take(10).ToList(),
            labHighlights = labFindings.Take(20).ToList(),
            recentDocuments = documents.Take(10).Select(d => new
            {
                d.Id,
                d.Category,
                d.FileName,
                rawTextSummary = d.RawTextSummary,
                uploadedAt = d.UploadedAt
            }).ToList()
        });
    }

    [HttpPost("{patientId}/extract")]
    public async Task<IActionResult> UploadAndExtractReal(int patientId, [FromForm] DocumentUploadRequestDto request)
    {
        var file = request?.File;
        var category = request?.Category;
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No valid medical document or image was uploaded." });
        }

        var docCategory = string.IsNullOrEmpty(category) ? "Prescription" : category;
        var patientFolder = Path.Combine(_uploadRootDirectory, $"patient_{patientId}");
        if (!Directory.Exists(patientFolder))
        {
            Directory.CreateDirectory(patientFolder);
        }

        // 1. Save REAL file physically to server disk archive
        var uniqueFileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{file.FileName}";
        var physicalFilePath = Path.Combine(patientFolder, uniqueFileName);

        using (var stream = new FileStream(physicalFilePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // 2. Perform REAL OCR / Text Extraction using Tesseract OCR or UglyToad.PdfPig
        string rawExtractedText = await ExtractTextWithTesseractOrPdfAsync(physicalFilePath, file.FileName);

        // If category is null, "null", or unselected, auto-detect from OCR text / filename
        if (string.IsNullOrWhiteSpace(docCategory) || docCategory.Equals("null", StringComparison.OrdinalIgnoreCase) || docCategory.Equals("undefined", StringComparison.OrdinalIgnoreCase))
        {
            docCategory = AutoDetectDocumentCategory(rawExtractedText, file.FileName);
        }

        // 3. Run REAL C# Clinical NLP Regex Parser on the extracted text
        var parsedDiagnoses = ExtractDiagnosesFromText(rawExtractedText, file.FileName, docCategory);
        var parsedMedications = docCategory.Equals("LabReport", StringComparison.OrdinalIgnoreCase) || docCategory.Equals("Radiology", StringComparison.OrdinalIgnoreCase)
            ? new List<MedicationItemDto>()
            : ExtractMedicationsFromText(rawExtractedText, file.FileName, docCategory);
        var parsedLabFindings = docCategory.Equals("Prescription", StringComparison.OrdinalIgnoreCase) || docCategory.Equals("Radiology", StringComparison.OrdinalIgnoreCase)
            ? new List<LabFindingItemDto>()
            : ExtractLabFindingsFromText(rawExtractedText, file.FileName, docCategory);
        var parsedRadiologyImpression = docCategory.Equals("Prescription", StringComparison.OrdinalIgnoreCase) || docCategory.Equals("LabReport", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ExtractRadiologyImpressionFromText(rawExtractedText, file.FileName, docCategory);

        // Build proper, short, professional clinical summary
        string summary;
        if (parsedMedications.Count > 0 || docCategory.Equals("Prescription", StringComparison.OrdinalIgnoreCase))
        {
            var medNames = parsedMedications.Select(m => m.MedicineName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            string medList = medNames.Any() ? string.Join(", ", medNames.Take(3)) + (medNames.Count > 3 ? $" (+{medNames.Count - 3} more)" : "") : "No medicines detected";
            string dx = parsedDiagnoses.FirstOrDefault() ?? string.Empty;
            summary = !string.IsNullOrWhiteSpace(dx)
                ? $"Rx: {parsedMedications.Count} Meds ({medList}) | Dx: {dx}"
                : $"Rx: {parsedMedications.Count} Meds ({medList})";
        }
        else if (parsedLabFindings.Count > 0 || docCategory.Equals("LabReport", StringComparison.OrdinalIgnoreCase))
        {
            var testNames = parsedLabFindings.Select(l => l.TestName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            string testList = testNames.Any() ? string.Join(", ", testNames.Take(3)) + (testNames.Count > 3 ? $" (+{testNames.Count - 3} more)" : "") : "No markers detected";
            summary = $"Lab Report: {parsedLabFindings.Count} Markers tested ({testList})";
        }
        else if (!string.IsNullOrWhiteSpace(parsedRadiologyImpression) || docCategory.Equals("Radiology", StringComparison.OrdinalIgnoreCase))
        {
            var imp = !string.IsNullOrWhiteSpace(parsedRadiologyImpression) ? parsedRadiologyImpression.Trim() : "Attached scan report";
            if (imp.Length > 70) imp = imp.Substring(0, 70) + "...";
            summary = $"Radiology: {imp}";
        }
        else if (!string.IsNullOrWhiteSpace(rawExtractedText) && rawExtractedText.Length > 20)
        {
            summary = $"Document scanned ({docCategory}). Review text or add records manually.";
        }
        else
        {
            summary = $"Attached {docCategory} document. Pending manual verification.";
        }

        // ⚡ Groq AI Direct Document Summarizer (Generates 100% exact summary directly from raw OCR text without touching auto-fill)
        if (!string.IsNullOrWhiteSpace(rawExtractedText) && rawExtractedText.Length > 25)
        {
            string aiDocPrompt = $"Here is the raw text extracted via OCR from an uploaded medical document (Title: {file.FileName}, Category: {docCategory}):\n\n\"\"\"{rawExtractedText}\"\"\"\n\nTask: Write a concise, 100% accurate, professional 2-sentence clinical summary of what exactly this document states (key diagnoses, clinical notes, findings, or prescribed medications). Do not invent anything not in the text.";
            summary = await GenerateClinicalSummaryWithGroqAsync(aiDocPrompt, fallbackText: summary);
        }

        // 4. Create REAL Database Entry for the uploaded document (`PatientDocuments`)
        var patientDoc = new PatientDocument
        {
            PatientId = patientId,
            Category = docCategory,
            FileName = file.FileName,
            FilePath = physicalFilePath,
            RawTextSummary = summary,
            UploadedAt = DateTime.UtcNow
        };
        _context.PatientDocuments.Add(patientDoc);
        await _context.SaveChangesAsync();

        // 5. Build web-accessible file URL for frontend preview
        var wwwrootBasePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        string? fileUrl = null;
        if (physicalFilePath.StartsWith(wwwrootBasePath, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = physicalFilePath.Substring(wwwrootBasePath.Length).Replace("\\", "/");
            fileUrl = relativePath;
        }

        // Return ONLY real extracted data — no fake/hardcoded values
        return Ok(new
        {
            documentId = patientDoc.Id,
            category = docCategory,
            documentTitle = file.FileName,
            extractedDate = DateTime.Now.ToString("yyyy-MM-dd"),
            fileUrl,
            doctorName = ExtractDoctorNameFromText(rawExtractedText),
            hospitalName = ExtractHospitalNameFromText(rawExtractedText),
            diagnoses = parsedDiagnoses,
            medications = parsedMedications,
            labFindings = parsedLabFindings,
            radiologyImpression = parsedRadiologyImpression,
            rawTextSummary = summary,
            rawOcrText = rawExtractedText
        });
    }

    [HttpPost("{patientId}/batch-save")]
    public async Task<IActionResult> BatchSaveRecordsToDatabase(int patientId, [FromBody] BatchSaveRequestDto request)
    {
        if (request == null)
            return BadRequest(new { message = "Invalid record save payload." });

        await EnsureDatabaseSchemaUpdatedAsync();
        int medsSaved = 0, labsSaved = 0, radiologySaved = 0;

        if (request.Medications != null && request.Medications.Any())
        {
            var medEntities = request.Medications
                .Where(m => !string.IsNullOrWhiteSpace(m.MedicineName)) // Skip empty rows
                .Select(m => new PatientMedication
                {
                    PatientId = patientId,
                    MedicineName = m.MedicineName.Trim(),
                    Dosage = m.Dosage?.Trim() ?? string.Empty,
                    Frequency = m.Frequency?.Trim() ?? string.Empty,
                    Duration = m.Duration?.Trim(),
                    CreatedAt = DateTime.UtcNow
                });
            _context.PatientMedications.AddRange(medEntities);
            medsSaved = medEntities.Count();
        }

        if (request.LabFindings != null && request.LabFindings.Any())
        {
            var labEntities = request.LabFindings
                .Where(l => !string.IsNullOrWhiteSpace(l.TestName)) // Skip empty rows
                .Select(l => new PatientLabFinding
                {
                    PatientId = patientId,
                    TestName = l.TestName.Trim(),
                    ObservedValue = l.ObservedValue?.Trim() ?? string.Empty,
                    ReferenceRange = l.ReferenceRange?.Trim() ?? string.Empty,
                    Unit = l.Unit?.Trim(),
                    Status = l.Status?.Trim() ?? "Normal",
                    Category = l.Category?.Trim() ?? "General",
                    IsAbnormal = l.IsAbnormal,
                    CreatedAt = DateTime.UtcNow
                });
            _context.PatientLabFindings.AddRange(labEntities);
            labsSaved = labEntities.Count();
        }

        if (!string.IsNullOrWhiteSpace(request.RadiologyImpression))
        {
            var radEntity = new PatientRadiologyNote
            {
                PatientId = patientId,
                ImpressionText = request.RadiologyImpression.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            _context.PatientRadiologyNotes.Add(radEntity);
            radiologySaved = 1;
        }

        // Update the latest uploaded document summary to a proper short clinical summary
        var latestDoc = await _context.PatientDocuments
            .Where(d => d.PatientId == patientId)
            .OrderByDescending(d => d.UploadedAt)
            .FirstOrDefaultAsync();

        if (latestDoc != null)
        {
            if (medsSaved > 0)
            {
                var medList = request.Medications!.Select(m => m.MedicineName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                string namesStr = string.Join(", ", medList.Take(3)) + (medList.Count > 3 ? $" (+{medList.Count - 3} more)" : "");
                latestDoc.RawTextSummary = $"Rx: {medsSaved} Meds ({namesStr})";
            }
            else if (labsSaved > 0)
            {
                var labList = request.LabFindings!.Select(l => l.TestName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                string namesStr = string.Join(", ", labList.Take(3)) + (labList.Count > 3 ? $" (+{labList.Count - 3} more)" : "");
                latestDoc.RawTextSummary = $"Lab Report: {labsSaved} Markers tested ({namesStr})";
            }
        }

        await _context.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = $"Successfully saved {medsSaved} medicines, {labsSaved} lab results, and {radiologySaved} radiology reports into EMR Database for Patient #{patientId}."
        });
    }

    // ==================================================================================
    // REAL OCR TEXT EXTRACTION (Tesseract for images, PdfPig for PDFs)
    // ==================================================================================

    private async Task<string> ExtractTextWithTesseractOrPdfAsync(string filePath, string fileName)
    {
        try
        {
            if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // First try extracting selectable text from PDF
                var textBuilder = new StringBuilder();
                using (var document = UglyToad.PdfPig.PdfDocument.Open(filePath))
                {
                    foreach (var page in document.GetPages())
                    {
                        textBuilder.AppendLine(page.Text);
                    }
                }

                var pdfText = textBuilder.ToString().Trim();

                // If PDF had selectable text, return it directly
                if (!string.IsNullOrWhiteSpace(pdfText) && pdfText.Length > 10)
                {
                    return pdfText;
                }

                // Scanned PDF with no selectable text — run Tesseract OCR on embedded images
                var tessDataPath = Path.Combine(Directory.GetCurrentDirectory(), "tessdata");
                if (Directory.Exists(tessDataPath))
                {
                    using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                    engine.SetVariable("tessedit_char_whitelist",
                        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,;:/-()%@ '\"\n\r\t");
                    engine.SetVariable("preserve_interword_spaces", "1");

                    using var document = UglyToad.PdfPig.PdfDocument.Open(filePath);
                    foreach (var page in document.GetPages())
                    {
                        foreach (var img in page.GetImages())
                        {
                            try
                            {
                                if (img.TryGetPng(out var pngBytes) && pngBytes != null && pngBytes.Length > 0)
                                {
                                    var tempPath = Path.Combine(Path.GetTempPath(), $"ocr_pdf_{Guid.NewGuid()}.png");
                                    await System.IO.File.WriteAllBytesAsync(tempPath, pngBytes);
                                    try
                                    {
                                        using var pix = Pix.LoadFromFile(tempPath);
                                        if (pix != null)
                                        {
                                            using var grayImg = pix.ConvertRGBToGray() ?? pix;
                                            using var pageOcr = engine.Process(grayImg);
                                            textBuilder.AppendLine(pageOcr.GetText());
                                        }
                                    }
                                    finally
                                    {
                                        if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
                                    }
                                }
                                else if (img.RawBytes != null && img.RawBytes.Count > 0)
                                {
                                    var tempPath = Path.Combine(Path.GetTempPath(), $"ocr_pdf_{Guid.NewGuid()}.jpg");
                                    await System.IO.File.WriteAllBytesAsync(tempPath, img.RawBytes.ToArray());
                                    try
                                    {
                                        using var pix = Pix.LoadFromFile(tempPath);
                                        if (pix != null)
                                        {
                                            using var grayImg = pix.ConvertRGBToGray() ?? pix;
                                            using var pageOcr = engine.Process(grayImg);
                                            textBuilder.AppendLine(pageOcr.GetText());
                                        }
                                    }
                                    catch
                                    {
                                        // Some raw bytes may not be direct JPEGs
                                    }
                                    finally
                                    {
                                        if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
                                    }
                                }
                            }
                            catch (Exception imgEx)
                            {
                                Console.WriteLine($"[PDF Image OCR Error] {imgEx.Message}");
                            }
                        }
                    }
                }

                pdfText = textBuilder.ToString().Trim();
                return pdfText;
            }
            else if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return await System.IO.File.ReadAllTextAsync(filePath);
            }
            else
            {
                // Real Tesseract OCR Execution on Image Files (.jpg, .png, .jpeg, .tiff)
                var tessDataPath = Path.Combine(Directory.GetCurrentDirectory(), "tessdata");
                if (!Directory.Exists(tessDataPath))
                {
                    return string.Empty;
                }

                using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);

                // Set Tesseract variables for better medical document OCR accuracy
                engine.SetVariable("tessedit_char_whitelist",
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,;:/-()%@ '\"\n\r\t");
                engine.SetVariable("preserve_interword_spaces", "1");

                using var img = Pix.LoadFromFile(filePath);

                // Preprocess image for better OCR accuracy:
                // Convert to grayscale then binarize (Otsu threshold)
                using var grayImg = img.ConvertRGBToGray();
                Pix? binarizedImg = null;
                try
                {
                    if (grayImg != null)
                    {
                        binarizedImg = grayImg.BinarizeOtsuAdaptiveThreshold(200, 200, 0, 0, 0.0f);
                    }
                }
                catch
                {
                    // Binarization may fail for some image formats — continue with original
                }

                // Use preprocessed image if available, otherwise fall back to original
                var processImg = binarizedImg ?? grayImg ?? img!;
                using var page = engine.Process(processImg);

                var confidence = page.GetMeanConfidence();
                var text = page.GetText()?.Trim() ?? string.Empty;

                // If preprocessed result is poor, try original image
                if ((string.IsNullOrWhiteSpace(text) || text.Length < 10 || confidence < 0.3))
                {
                    using var fallbackPage = engine.Process(img);
                    var fallbackText = fallbackPage.GetText()?.Trim() ?? string.Empty;
                    if (fallbackText.Length > text.Length)
                    {
                        text = fallbackText;
                    }
                }

                binarizedImg?.Dispose();

                return text;
            }
        }
        catch (Exception ex)
        {
            // Return empty string on failure — frontend will show manual entry option
            Console.WriteLine($"[OCR Error] {ex.Message}");
            return string.Empty;
        }
    }

    // ==================================================================================
    // CLINICAL NLP REGEX PARSERS — Extract structured data from raw OCR text
    // ==================================================================================

    private List<string> ExtractDiagnosesFromText(string text, string fileName = "", string category = "")
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        // Pattern 1: "Diagnosis: ..." or "Dx: ..." or "Diagnosed with: ..."
        var diagPatterns = new[]
        {
            @"(?i)(?:Diagnosis|Diagnosed\s+with|Dx|Clinical\s+Diagnosis|Provisional\s+Diagnosis|Final\s+Diagnosis)\s*[:=\-]\s*([^\r\n]{3,80})",
            @"(?i)(?:Condition|Chief\s+Complaint|Presenting\s+Complaint|C/O)\s*[:=\-]\s*([^\r\n]{3,80})"
        };

        foreach (var pattern in diagPatterns)
        {
            var matches = Regex.Matches(text, pattern);
            foreach (Match match in matches)
            {
                if (match.Success && match.Groups[1].Success)
                {
                    var val = match.Groups[1].Value.Trim().TrimEnd('.', ',', ';');
                    if (val.Length >= 3 && !list.Contains(val, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(val);
                    }
                }
            }
        }
        return list;
    }

    private string CleanCoreMedicineName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var cleaned = Regex.Replace(name, @"^(?:Tab\.?|Cap\.?|Syp\.?|Inj\.?|Tablet|Capsule|Syrup|Injection|Ointment|Drops)[\s,]+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"[\.\,\;\-\:\+]+$", "").Trim();
        return cleaned.ToLower();
    }

    private List<MedicationItemDto> ExtractMedicationsFromText(string text, string fileName = "", string category = "")
    {
        var list = new List<MedicationItemDto>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // STRATEGY 1: Line-by-Line Prescription Table & Numbered Row Parser
        // Handles: "# | Medicine | Strength | Dosage | Duration | Instructions"
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length < 4) continue;

            // Ignore headers and non-prescription lines
            if (line.StartsWith("Patient", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Medicine", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Strength", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Dosage", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Duration", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Instructions", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Date", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Dr.", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Diagnosis", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Disease", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Note", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Reg. No", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Bring ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Thank ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Mon ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Sun ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if line looks like a numbered row ("1.", "2.") OR starts with Tab/Cap/Syp/Inj OR contains known medicine keywords
            bool hasPrefixOrNumber = Regex.IsMatch(line, @"(?i)^(?:[\#\|\s\.\-\*]*\d+[\.\)\-\:\|\s]*)?(?:Tab\.?|Cap\.?|Syp\.?|Inj\.?|Tablet|Capsule|Syrup|Injection|Ointment|Drops)\b") ||
                                     Regex.IsMatch(line, @"^(?:[\#\|\s\.\-\*]*\d+[\.\)\-\:\|\s]+)[A-Za-z]");

            if (hasPrefixOrNumber)
            {
                // Strip row number (#.) and any leading symbols/pipes from start
                var cleanedLine = Regex.Replace(line, @"^[\#\|\s\.\-\*]*\d+[\.\)\-\:\|\s]*", "").Trim();

                // Find left dosage/strength match anywhere: digits with unit OR solitary hyphen/dash (' - ')
                var dosageMatch = Regex.Match(cleanedLine, @"(?<dosage>\b\d+(?:[\,\.\d]+)?\s*(?:mg|g|ml|mcg|IU|units?|%)\b|\s+[\-\–\—]\s+)", RegexOptions.IgnoreCase);

                int splitIndex = -1;
                string dosage = "";
                string postDosage = "";

                if (dosageMatch.Success && dosageMatch.Index > 1)
                {
                    splitIndex = dosageMatch.Index;
                    dosage = dosageMatch.Groups["dosage"].Value.Trim();
                    if (dosage.Equals("-") || dosage.Equals("–") || dosage.Equals("—")) dosage = "";
                    postDosage = cleanedLine.Substring(dosageMatch.Index + dosageMatch.Length).Trim();
                }
                else
                {
                    // If dosage not found or empty hyphen, check if frequency keywords exist to split
                    var freqMatch = Regex.Match(cleanedLine, @"(?<freq>\b(?:1|2|3)?\s*(?:Tablet|Capsule|Cap|Tab|OD|BD|TDS|QID|SOS|HS|daily|once|twice|thrice|morning|night)\b.*)", RegexOptions.IgnoreCase);
                    if (freqMatch.Success && freqMatch.Index > 1)
                    {
                        splitIndex = freqMatch.Index;
                        dosage = "";
                        postDosage = freqMatch.Value.Trim();
                    }
                }

                if (splitIndex > 1)
                {
                    // Everything to the left of splitIndex is Medicine Name (with optional prefix)
                    var fullMedName = cleanedLine.Substring(0, splitIndex).Trim().TrimEnd('.', ',', ';', '-', '–', '—', ' ');

                    string freq = postDosage;
                    string duration = "";

                    // Search inside postDosage for exact Duration
                    var durMatch = Regex.Match(postDosage, @"\b(?<dur>\d+\s*(?:Weeks?|Wks?|Days?|Months?|Mths?|Years?|Yrs?)|As\s+needed|PRN|Continue|Long\s+term|\d+\s*-\s*\d+\s*(?:Days?|Weeks?))\b", RegexOptions.IgnoreCase);
                    if (durMatch.Success)
                    {
                        duration = durMatch.Groups["dur"].Value.Trim();
                        if (duration.Equals("As needed", StringComparison.OrdinalIgnoreCase)) duration = "As needed";

                        // Everything inside postDosage before duration is exact Frequency
                        freq = postDosage.Substring(0, durMatch.Index).Trim();
                    }

                    // Clean trailing instruction phrases from Frequency if no duration was found or leftover
                    freq = Regex.Replace(freq, @"(?:\s+After\s+Food|\s+Before\s+Food|\s+With\s+Food|\s+After\s+Meals?|\s+Before\s+Meals?|\s+Before\s+breakfast)$", "", RegexOptions.IgnoreCase).Trim();
                    freq = NormalizeFrequency(freq);

                    string coreName = CleanCoreMedicineName(fullMedName);
                    if (fullMedName.Length >= 2 && coreName.Length >= 2 && !addedNames.Contains(coreName))
                    {
                        addedNames.Add(coreName);
                        list.Add(new MedicationItemDto
                        {
                            MedicineName = fullMedName,
                            Dosage = dosage,
                            Frequency = freq,
                            Duration = duration
                        });
                        continue;
                    }
                }
            }
        }

        // STRATEGY 2: General Patterns for non-table/loose text (if Strategy 1 missed anything)
        var medPatterns = new[]
        {
            @"(?i)(?:Tab\.?|Cap\.?|Syp\.?|Inj\.?|Tablet|Capsule|Syrup|Injection)\s+([A-Za-z][A-Za-z0-9\s\+\-\/\(\)\.,\[\]]{2,40}?)\s+(\d+(?:\.\d+)?\s*(?:mg|g|ml|mcg|IU|units?))",
            @"(?:^|\n)\s*\d+[\.\)]\s*([A-Za-z][A-Za-z0-9\s\+\-\/\(\)\.,\[\]]{2,40}?)\s+(\d+(?:\.\d+)?\s*(?:mg|g|ml|mcg|IU|units?))",
            @"(?i)\b([A-Za-z][A-Za-z\+\-\/\(\)]{2,30}(?:statin|formin|sartan|cillin|mycin|mab|prazole|lol|pril|dipine|pine|zole|fen|mine|rine|done|pam|zepam|oxin|xone|amine|etine|pride|glide|zide|acid))\s+(\d+(?:\.\d+)?\s*(?:mg|g|ml|mcg|IU|units?))"
        };

        foreach (var pattern in medPatterns)
        {
            var matches = Regex.Matches(text, pattern);
            foreach (Match match in matches)
            {
                if (!match.Success) continue;

                var rawName = match.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ' ');
                var dosage = match.Groups[2].Success ? match.Groups[2].Value.Trim() : string.Empty;

                string coreName = CleanCoreMedicineName(rawName);
                if (rawName.Length < 3 || coreName.Length < 2 || addedNames.Contains(coreName)) continue;

                var skipWords = new[] { "the", "and", "for", "with", "from", "this", "that", "patient", "report", "test", "date", "name", "age", "medicine", "strength", "dosage", "duration" };
                if (skipWords.Any(w => w.Equals(coreName, StringComparison.OrdinalIgnoreCase))) continue;

                // For Strategy 2 matches, find postDosage inside text
                string frequency = "";
                string duration = "";
                int postIndex = match.Index + match.Length;
                if (postIndex < text.Length)
                {
                    // Take up to next line break or 80 characters after dosage
                    int endLineIdx = text.IndexOfAny(new[] { '\r', '\n' }, postIndex);
                    string postText = endLineIdx != -1 ? text.Substring(postIndex, endLineIdx - postIndex) : text.Substring(postIndex, Math.Min(80, text.Length - postIndex));
                    postText = postText.Trim();

                    var durMatch = Regex.Match(postText, @"\b(?<dur>\d+\s*(?:Weeks?|Wks?|Days?|Months?|Mths?|Years?|Yrs?)|As\s+needed|PRN|Continue|Long\s+term|\d+\s*-\s*\d+\s*(?:Days?|Weeks?))\b", RegexOptions.IgnoreCase);
                    if (durMatch.Success)
                    {
                        duration = durMatch.Groups["dur"].Value.Trim();
                        if (duration.Equals("As needed", StringComparison.OrdinalIgnoreCase)) duration = "As needed";
                        frequency = postText.Substring(0, durMatch.Index).Trim();
                    }
                    else
                    {
                        frequency = postText;
                    }
                    frequency = Regex.Replace(frequency, @"(?:\s+After\s+Food|\s+Before\s+Food|\s+With\s+Food|\s+After\s+Meals?|\s+Before\s+Meals?|\s+Before\s+breakfast)$", "", RegexOptions.IgnoreCase).Trim();
                    frequency = NormalizeFrequency(frequency);
                }

                addedNames.Add(coreName);
                list.Add(new MedicationItemDto
                {
                    MedicineName = rawName,
                    Dosage = dosage,
                    Frequency = frequency,
                    Duration = duration
                });
            }
        }
        return list;
    }

    private List<LabFindingItemDto> ExtractLabFindingsFromText(string text, string fileName = "", string category = "")
    {
        var list = new List<LabFindingItemDto>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var addedTests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string currentSection = "General";

        // Dictionary of known clinical lab test keywords for ultra-accurate matching
        var knownTests = new[]
        {
            "Hemoglobin (Hb)", "Hemoglobin", "Haemoglobin", "HbA1c", "Total WBC Count", "WBC Count", "WBC", "RBC Count", "RBC",
            "Platelet Count", "Platelets", "ESR (Westergren)", "ESR", "C-Reactive Protein (CRP)", "C-Reactive Protein", "CRP",
            "Rheumatoid Factor (RF)", "Rheumatoid Factor", "RF", "Anti-CCP Antibody", "Anti-CCP", "ANA (Antinuclear Antibody)", "ANA",
            "Vitamin D (25-OH)", "Vitamin D", "25-OH Vitamin D", "Blood Sugar (Fasting)", "Blood Sugar", "Fasting Sugar", "FBS", "PP Sugar",
            "Serum Uric Acid", "Uric Acid", "Creatinine", "Serum Creatinine", "SGOT (AST)", "SGOT", "AST", "SGPT (ALT)", "SGPT", "ALT",
            "TSH (Thyroid Stimulating Hormone)", "TSH", "Iron", "Serum Iron", "Total Cholesterol", "Cholesterol", "HDL", "LDL", "Triglycerides",
            "Alkaline Phosphatase", "ALP", "Bilirubin Total", "Bilirubin Direct", "Albumin", "Total Protein", "Serum Calcium", "Calcium",
            "Sodium", "Potassium", "Chloride", "Ferritin", "D-Dimer"
        };

        // Known medical units
        var medicalUnits = new[]
        {
            "g/dL", "gm/dL", "mg/dL", "mg%", "g%", "%", "cells/cumm", "million/cumm", "Lakh/cumm", "thou/cumm",
            "x10^3/uL", "x10^6/uL", "mm/hr", "U/L", "IU/L", "IU/mL", "U/mL", "ng/mL", "pg/mL", "uIU/mL", "µIU/mL",
            "ug/dL", "µg/dL", "mcg/dL", "mmol/L", "mEq/L", "fL", "pg", "cells/ul"
        };

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length < 3) continue;

            // Track sections
            if (line.Equals("Hematology", StringComparison.OrdinalIgnoreCase) || line.Contains("Hematology", StringComparison.OrdinalIgnoreCase)) { currentSection = "Hematology"; continue; }
            if (line.Equals("Biochemistry", StringComparison.OrdinalIgnoreCase) || line.Contains("Biochemistry", StringComparison.OrdinalIgnoreCase)) { currentSection = "Biochemistry"; continue; }
            if (line.Contains("Inflammation", StringComparison.OrdinalIgnoreCase) || line.Contains("Autoimmune", StringComparison.OrdinalIgnoreCase)) { currentSection = "Inflammation / Autoimmune Markers"; continue; }
            if (line.Contains("Serology", StringComparison.OrdinalIgnoreCase) || line.Contains("Thyroid", StringComparison.OrdinalIgnoreCase)) { currentSection = "Serology / Others"; continue; }

            // Immediately skip headers, footers, demographic, and ID lines
            var lower = line.ToLowerInvariant();
            if (lower.StartsWith("patient") || lower.StartsWith("report") || lower.StartsWith("sample") ||
                lower.StartsWith("lab name") || lower.StartsWith("test name") || lower.StartsWith("doctor") ||
                lower.StartsWith("dr.") || lower.StartsWith("page") || lower.StartsWith("date") ||
                lower.StartsWith("time") || lower.StartsWith("age") || lower.StartsWith("sex") ||
                lower.StartsWith("gender") || lower.StartsWith("ref by") || lower.StartsWith("referred") ||
                lower.StartsWith("id") || lower.StartsWith("reg no") || lower.StartsWith("barcode") ||
                lower.StartsWith("collected") || lower.StartsWith("authorized") || lower.StartsWith("checked by") ||
                lower.Contains("www.") || lower.Contains(".com") || lower.Contains("end of report") ||
                lower.StartsWith("sr.") || lower.StartsWith("no.") || lower.Contains("test results") || lower.Contains("important note"))
            {
                continue;
            }

            string testName = "";
            string val = "";
            string unit = "";
            string range = "Standard Range";
            string status = "Normal";
            bool found = false;

            // STRATEGY A: Check against Known Tests first for exact targeted extraction
            foreach (var kt in knownTests)
            {
                var ktIdx = line.IndexOf(kt, StringComparison.OrdinalIgnoreCase);
                if (ktIdx != -1)
                {
                    var postText = line.Substring(ktIdx + kt.Length).Trim().TrimStart(':', '=', '-', '.').Trim();
                    var valMatch = Regex.Match(postText, @"^(?<val>\d{1,6}(?:,\d{3})*(?:\.\d+)?|Negative|Positive|Reactive|Non-Reactive)\b(?:\s+(?<unit>[A-Za-z0-9\/\^\%\µu]+))?(?:\s+(?<range>[0-9\.\,\-\<\>\s]{1,25}|Negative|Normal|Non-Reactive))?(?:\s+(?<status>Normal|High|Low|Positive|Negative|Deficient|Insufficient|Sufficient|Abnormal|Reactive))?", RegexOptions.IgnoreCase);
                    if (valMatch.Success && valMatch.Groups["val"].Success)
                    {
                        testName = kt;
                        val = valMatch.Groups["val"].Value.Trim();
                        var parsedUnit = valMatch.Groups["unit"].Success ? valMatch.Groups["unit"].Value.Trim() : "";
                        if (medicalUnits.Any(u => u.Equals(parsedUnit, StringComparison.OrdinalIgnoreCase)) || parsedUnit.Equals("-"))
                        {
                            unit = parsedUnit;
                        }
                        else if (string.IsNullOrEmpty(unit))
                        {
                            var foundUnit = medicalUnits.FirstOrDefault(u => postText.Contains(u, StringComparison.OrdinalIgnoreCase));
                            if (foundUnit != null) unit = foundUnit;
                        }

                        if (valMatch.Groups["range"].Success && !string.IsNullOrWhiteSpace(valMatch.Groups["range"].Value))
                        {
                            var r = valMatch.Groups["range"].Value.Trim();
                            if (!r.Equals("Normal", StringComparison.OrdinalIgnoreCase) && !r.Equals("High", StringComparison.OrdinalIgnoreCase) && !r.Equals("Low", StringComparison.OrdinalIgnoreCase) && !r.Equals("Deficient", StringComparison.OrdinalIgnoreCase) && !r.Equals("Positive", StringComparison.OrdinalIgnoreCase))
                            {
                                range = r;
                            }
                        }

                        // Determine status
                        if (valMatch.Groups["status"].Success && !string.IsNullOrWhiteSpace(valMatch.Groups["status"].Value))
                        {
                            status = valMatch.Groups["status"].Value.Trim();
                        }
                        else
                        {
                            if (postText.Contains("High", StringComparison.OrdinalIgnoreCase)) status = "High";
                            else if (postText.Contains("Low", StringComparison.OrdinalIgnoreCase)) status = "Low";
                            else if (postText.Contains("Positive", StringComparison.OrdinalIgnoreCase)) status = "Positive";
                            else if (postText.Contains("Deficient", StringComparison.OrdinalIgnoreCase)) status = "Deficient";
                            else if (val.Equals("Negative", StringComparison.OrdinalIgnoreCase)) status = "Negative";
                            else if (val.Equals("Positive", StringComparison.OrdinalIgnoreCase)) status = "Positive";
                        }

                        found = true;
                        break;
                    }
                }
            }

            // STRATEGY B: Flexible Table Row match (if not matched by Strategy A)
            if (!found)
            {
                var match = Regex.Match(line, @"^(?<name>[A-Za-z][A-Za-z0-9\s\(\)\-\/\+]{2,45}?)\s*(?:[:=\-]\s*)?(?<val>\d{1,6}(?:,\d{3})*(?:\.\d+)?|Negative|Positive|Reactive|Non-Reactive)\s+(?<unit>g\/dL|gm\/dL|mg\/dL|mg%|g%|%|cells\/cumm|million\/cumm|Lakh\/cumm|thou\/cumm|mm\/hr|U\/L|IU\/L|IU\/mL|U\/mL|ng\/mL|pg\/mL|uIU\/mL|µIU\/mL|ug\/dL|µg\/dL|mcg\/dL|mmol\/L|mEq\/L|fL|pg|cells\/ul|x10\^3\/ul)\b(?:\s+(?<range>[0-9\.\,\-\<\>\s]{1,20}|Negative|Normal|Non-Reactive))?(?:\s+(?<status>Normal|High|Low|Positive|Negative|Deficient|Insufficient|Sufficient|Abnormal|Reactive))?", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    testName = match.Groups["name"].Value.Trim().TrimEnd('.', ',', ':', '=', '-', ' ');
                    val = match.Groups["val"].Value.Trim();
                    unit = match.Groups["unit"].Value.Trim();
                    if (match.Groups["range"].Success && !string.IsNullOrWhiteSpace(match.Groups["range"].Value)) range = match.Groups["range"].Value.Trim();
                    if (match.Groups["status"].Success && !string.IsNullOrWhiteSpace(match.Groups["status"].Value)) status = match.Groups["status"].Value.Trim();
                    found = true;
                }
            }

            if (found && !string.IsNullOrWhiteSpace(testName) && testName.Length >= 2 && !addedTests.Contains(testName))
            {
                var skipWords = new[] { "page", "date", "time", "age", "sex", "name", "ref", "no", "sr", "status", "test", "result", "unit", "comment" };
                if (skipWords.Any(w => w.Equals(testName, StringComparison.OrdinalIgnoreCase))) continue;

                addedTests.Add(testName);

                bool isAbnormal = status.Equals("High", StringComparison.OrdinalIgnoreCase) ||
                                  status.Equals("Low", StringComparison.OrdinalIgnoreCase) ||
                                  status.Equals("Positive", StringComparison.OrdinalIgnoreCase) ||
                                  status.Equals("Deficient", StringComparison.OrdinalIgnoreCase) ||
                                  status.Equals("Abnormal", StringComparison.OrdinalIgnoreCase) ||
                                  val.Equals("Positive", StringComparison.OrdinalIgnoreCase);

                if (status.Equals("Normal", StringComparison.OrdinalIgnoreCase) && isAbnormal)
                {
                    status = val.Equals("Positive", StringComparison.OrdinalIgnoreCase) ? "Positive" : "High / Abnormal";
                }

                list.Add(new LabFindingItemDto
                {
                    TestName = testName,
                    ObservedValue = val,
                    Unit = unit,
                    ReferenceRange = range,
                    Status = status,
                    Category = currentSection,
                    IsAbnormal = isAbnormal
                });
            }
        }
        return list;
    }

    private string AutoDetectDocumentCategory(string text, string fileName = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return "Prescription";
        var radioKeywords = new[] { "x-ray", "xray", "mri", "ct scan", "ultrasound", "usg", "sonography", "fracture", "effusion", "lesion", "mass", "disc", "vertebra", "radiologist", "imaging centre", "diagnostic centre", "impression:", "findings:" };
        if (radioKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)) || fileName.Contains("mri", StringComparison.OrdinalIgnoreCase) || fileName.Contains("xray", StringComparison.OrdinalIgnoreCase) || fileName.Contains("ct", StringComparison.OrdinalIgnoreCase) || fileName.Contains("scan", StringComparison.OrdinalIgnoreCase))
        {
            return "Radiology";
        }
        var labKeywords = new[] { "hemoglobin", "haemoglobin", "wbc", "rbc", "platelet", "blood sugar", "cholesterol", "triglycerides", "creatinine", "uric acid", "observed value", "reference range", "unit", "lab report" };
        if (labKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)) || fileName.Contains("lab", StringComparison.OrdinalIgnoreCase) || fileName.Contains("report", StringComparison.OrdinalIgnoreCase))
        {
            return "LabReport";
        }
        return "Prescription";
    }

    private string? ExtractRadiologyImpressionFromText(string text, string fileName = "", string category = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sections = new List<string>();

        // 1. Try to capture the IMPRESSION / CONCLUSION / OPINION section specifically
        var impMatch = Regex.Match(text, @"(?i)(?:^|\n)\s*(?:IMPRESSION|CONCLUSION|OPINION|INTERPRETATION)\s*[:=\-]\s*([\s\S]+?)(?=(?:\r?\n\s*(?:Note\b|Dr\.?\b|Reporting\s+Dr|MD\b|Consultant\b|Reg\.?\s*No|FINDINGS\b|Thank\s+you|Scan\s+Date|Patient\s+Name)|$))");
        if (impMatch.Success && impMatch.Groups[1].Value.Trim().Length >= 10)
        {
            var impText = CleanSectionText(impMatch.Groups[1].Value);
            sections.Add($"IMPRESSION:\n{impText}");
        }

        // 2. Try to capture the FINDINGS / REPORT / COMMENTS section specifically
        var findMatch = Regex.Match(text, @"(?i)(?:^|\n)\s*(?:FINDINGS|REPORT|COMMENTS|OBSERVATIONS)\s*[:=\-]\s*([\s\S]+?)(?=(?:\r?\n\s*(?:IMPRESSION\b|CONCLUSION\b|OPINION\b|INTERPRETATION\b|Note\b|Dr\.?\b|Reporting\s+Dr|MD\b|Consultant\b|Reg\.?\s*No|Thank\s+you)|$))");
        if (findMatch.Success && findMatch.Groups[1].Value.Trim().Length >= 10)
        {
            var findText = CleanSectionText(findMatch.Groups[1].Value);
            sections.Add($"FINDINGS:\n{findText}");
        }

        if (sections.Any())
        {
            return string.Join("\n\n--------------------------------------------------\n\n", sections);
        }

        // 3. Fallback: If no section header was found, but category is Radiology or text has radiology keywords
        var radioKeywords = new[] { "x-ray", "xray", "mri", "ct scan", "ultrasound", "usg", "sonography",
                                    "fracture", "opacity", "effusion", "lesion", "mass", "disc", "vertebra",
                                    "chest", "abdomen", "spine", "brain", "lung", "knee", "joint" };
        if (category.Equals("Radiology", StringComparison.OrdinalIgnoreCase) || radioKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            var cleaned = CleanSectionText(text);
            return cleaned.Length > 1200 ? cleaned.Substring(0, 1200).Trim() + "\n...[Additional text truncated]" : cleaned;
        }

        return null;
    }

    private string CleanSectionText(string sectionText)
    {
        if (string.IsNullOrWhiteSpace(sectionText)) return "";
        var lines = sectionText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 1 && !Regex.IsMatch(l, @"^\d+$") && !l.StartsWith("Page ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return string.Join("\n", lines);
    }

    private string? ExtractDoctorNameFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var patterns = new[]
        {
            @"(?i)(?:Dr\.?\s*|Doctor\s+|Physician\s*[:=\-]\s*)([A-Za-z][A-Za-z\s\.]{2,35})",
            @"(?i)(?:Consulting|Attending|Referred\s+by|Seen\s+by)\s*[:=\-]?\s*(?:Dr\.?\s*)?([A-Za-z][A-Za-z\s\.]{2,35})"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern);
            if (match.Success && match.Groups[1].Success)
            {
                var name = match.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ' ');
                if (name.Length >= 3)
                {
                    return name;
                }
            }
        }
        return null;
    }

    private string? ExtractHospitalNameFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var patterns = new[]
        {
            @"(?i)([A-Za-z][A-Za-z\s&\.]{3,45}(?:Hospital|Clinic|Diagnostics?|Laboratory|Labs?|Healthcare|Center|Centre|Medical|Nursing\s+Home|Pathology|Polyclinic))",
            @"(?i)(?:Lab|Laboratory|Diagnostic)\s*[:=\-]?\s*([A-Za-z][A-Za-z\s&\.]{3,40})"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ' ');
                if (name.Length >= 5)
                {
                    return name;
                }
            }
        }
        return null;
    }

    // ==================================================================================
    // HELPERS
    // ==================================================================================

    [HttpDelete("{patientId}/document/{docId}")]
    public async Task<IActionResult> DeletePatientDocument(int patientId, int docId)
    {
        var doc = await _context.PatientDocuments.FirstOrDefaultAsync(d => d.Id == docId && d.PatientId == patientId);
        if (doc == null) return NotFound(new { message = "Document not found." });

        _context.PatientDocuments.Remove(doc);
        await _context.SaveChangesAsync();
        return Ok(new { message = "Document deleted successfully." });
    }

    [HttpDelete("{patientId}/medication/{medId}")]
    public async Task<IActionResult> DeletePatientMedication(int patientId, int medId)
    {
        var med = await _context.PatientMedications.FirstOrDefaultAsync(m => m.Id == medId && m.PatientId == patientId);
        if (med == null) return NotFound(new { message = "Medication record not found." });

        _context.PatientMedications.Remove(med);
        await _context.SaveChangesAsync();
        return Ok(new { message = "Medication deleted successfully." });
    }

    [HttpDelete("{patientId}/radiology/{radId}")]
    public async Task<IActionResult> DeletePatientRadiologyNote(int patientId, int radId)
    {
        var rad = await _context.PatientRadiologyNotes.FirstOrDefaultAsync(r => r.Id == radId && r.PatientId == patientId);
        if (rad == null) return NotFound(new { message = "Radiology record not found." });

        _context.PatientRadiologyNotes.Remove(rad);
        await _context.SaveChangesAsync();
        return Ok(new { message = "Radiology record deleted successfully." });
    }

    private async Task EnsureDatabaseSchemaUpdatedAsync()
    {
        try
        {
            await _context.Database.ExecuteSqlRawAsync(
                @"IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Status' AND Object_ID = Object_ID(N'PatientLabFindings'))
                  BEGIN ALTER TABLE PatientLabFindings ADD Status nvarchar(max) NOT NULL DEFAULT N'Normal'; END;
                  IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Category' AND Object_ID = Object_ID(N'PatientLabFindings'))
                  BEGIN ALTER TABLE PatientLabFindings ADD Category nvarchar(max) NOT NULL DEFAULT N'General'; END;
                  IF NOT EXISTS (SELECT * FROM sys.columns WHERE Name = N'Duration' AND Object_ID = Object_ID(N'PatientMedications'))
                  BEGIN ALTER TABLE PatientMedications ADD Duration nvarchar(max) NULL; END;");
        }
        catch { }
    }

    private static string NormalizeFrequency(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        raw = raw.Trim();

        // Normalize common Indian prescription frequency notations
        if (Regex.IsMatch(raw, @"1[\s\-]*0[\s\-]*1")) return "Twice daily (1-0-1)";
        if (Regex.IsMatch(raw, @"1[\s\-]*1[\s\-]*1")) return "Thrice daily (1-1-1)";
        if (Regex.IsMatch(raw, @"0[\s\-]*0[\s\-]*1")) return "Once at night (0-0-1)";
        if (Regex.IsMatch(raw, @"1[\s\-]*0[\s\-]*0")) return "Once in morning (1-0-0)";
        if (raw.Equals("OD", StringComparison.OrdinalIgnoreCase)) return "Once daily";
        if (raw.Equals("BD", StringComparison.OrdinalIgnoreCase)) return "Twice daily";
        if (raw.Equals("TDS", StringComparison.OrdinalIgnoreCase)) return "Thrice daily";
        if (raw.Equals("QID", StringComparison.OrdinalIgnoreCase)) return "Four times daily";
        if (raw.Equals("SOS", StringComparison.OrdinalIgnoreCase)) return "As needed (SOS)";
        if (raw.Equals("HS", StringComparison.OrdinalIgnoreCase)) return "At bedtime";
        if (raw.Equals("stat", StringComparison.OrdinalIgnoreCase)) return "Immediately (Stat)";
        if (raw.Equals("PRN", StringComparison.OrdinalIgnoreCase)) return "As needed (PRN)";

        return raw;
    }
}

public class MedicationItemDto
{
    public string MedicineName { get; set; } = string.Empty;
    public string Dosage { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public string? Duration { get; set; }
}

public class LabFindingItemDto
{
    public string TestName { get; set; } = string.Empty;
    public string ObservedValue { get; set; } = string.Empty;
    public string ReferenceRange { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsAbnormal { get; set; }
}

public class BatchSaveRequestDto
{
    public List<MedicationItemDto>? Medications { get; set; }
    public List<LabFindingItemDto>? LabFindings { get; set; }
    public string? RadiologyImpression { get; set; }
}

public class DocumentUploadRequestDto
{
    public IFormFile? File { get; set; }
    public string? Category { get; set; }
}
