using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EMR.Application.Interfaces;
using EMR.Application.DTOs.Documents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EMR.Infrastructure.Services;

public class GroqWhisperService : IVoiceService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GroqWhisperService> _logger;
    
    private const string GROQ_AUDIO_URL = "https://api.groq.com/openai/v1/audio/transcriptions";

    public GroqWhisperService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<GroqWhisperService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string> TranscribeAudioAsync(IFormFile audioFile)
    {
        if (audioFile == null || audioFile.Length == 0)
        {
            throw new ArgumentException("Audio file is empty or null.");
        }

        string apiKey = _configuration["GroqSettings:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_GROQ_API_KEY_HERE")
        {
            _logger.LogError("Groq API Key is not configured correctly.");
            throw new InvalidOperationException("Groq API Key is missing. Transcription cannot proceed.");
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var content = new MultipartFormDataContent();
            
            // Add the model parameter
            content.Add(new StringContent("whisper-large-v3"), "model");
            
            // Note: Groq Whisper works well with medical terms, so adding prompt can help context
            content.Add(new StringContent("Medical consultation, prescription, lab test, patient symptoms."), "prompt");

            // Read the file stream and add it
            using var fileStream = audioFile.OpenReadStream();
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(audioFile.ContentType);
            
            // Give it a generic name if not provided
            string fileName = string.IsNullOrWhiteSpace(audioFile.FileName) ? "audio.webm" : audioFile.FileName;
            content.Add(streamContent, "file", fileName);

            _logger.LogInformation("Sending audio to Groq Whisper for transcription...");
            
            var response = await client.PostAsync(GROQ_AUDIO_URL, content);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Groq Whisper API failed: {statusCode} {error}", response.StatusCode, responseString);
                throw new Exception($"Transcription failed: {responseString}");
            }

            // Parse response JSON to get the text
            using var jsonDocument = JsonDocument.Parse(responseString);
            if (jsonDocument.RootElement.TryGetProperty("text", out var textElement))
            {
                return textElement.GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during audio transcription.");
            throw;
        }
    }

    public async Task<AiExtractedDocumentDto> ExtractVoiceDataAsync(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new AiExtractedDocumentDto();
        }

        string apiKey = _configuration["GroqSettings:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_GROQ_API_KEY_HERE")
        {
            _logger.LogError("Groq API Key is not configured correctly.");
            throw new InvalidOperationException("Groq API Key is missing. Extraction cannot proceed.");
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            string endpoint = "https://api.groq.com/openai/v1/chat/completions";

            var systemPrompt = @"You are a medical AI assistant. Extract clinical data from the following voice transcription into a structured JSON format. 
DO NOT add any other conversational text. Output ONLY valid JSON matching this exact structure:
{
    ""clinicalSummary"": ""string (Brief summary of the visit)"",
    ""diagnoses"": [""string""],
    ""medications"": [
        {
            ""medicineName"": ""string"",
            ""dosage"": ""string"",
            ""frequency"": ""string"",
            ""duration"": ""string""
        }
    ],
    ""labFindings"": [
        {
            ""testName"": ""string"",
            ""observedValue"": ""string"",
            ""referenceRange"": ""string"",
            ""isAbnormal"": boolean
        }
    ]
}
If any field is missing or not mentioned, return null or empty array.";

            var requestBody = new
            {
                model = "llama-3.3-70b-versatile",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = rawText }
                },
                temperature = 0.1,
                response_format = new { type = "json_object" }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await client.PostAsync(endpoint, jsonContent);
            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Groq LLaMA API failed: {statusCode} {error}", response.StatusCode, responseString);
                throw new Exception($"Extraction failed: {responseString}");
            }

            using var jsonDocument = JsonDocument.Parse(responseString);
            var root = jsonDocument.RootElement;

            if (root.TryGetProperty("choices", out var choicesElement) && choicesElement.GetArrayLength() > 0)
            {
                var contentString = choicesElement[0].GetProperty("message").GetProperty("content").GetString();
                if (!string.IsNullOrWhiteSpace(contentString))
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var extracted = JsonSerializer.Deserialize<AiExtractedDocumentDto>(contentString, options);
                    if (extracted != null)
                    {
                        return extracted;
                    }
                }
            }

            return new AiExtractedDocumentDto();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during voice data extraction.");
            throw;
        }
    }
}
