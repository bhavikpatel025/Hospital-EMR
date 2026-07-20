using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace EMR.Application.Interfaces;

public interface IVoiceService
{
    Task<string> TranscribeAudioAsync(IFormFile audioFile);
}
