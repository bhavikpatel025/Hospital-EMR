using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using EMR.Application.DTOs.Documents;

namespace EMR.Application.Interfaces;

public interface IVoiceService
{
    Task<string> TranscribeAudioAsync(IFormFile audioFile);
    Task<AiExtractedDocumentDto> ExtractVoiceDataAsync(string rawText);
}
