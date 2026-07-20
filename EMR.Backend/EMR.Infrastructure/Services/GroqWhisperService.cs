using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using EMR.Application.Interfaces;
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
}
