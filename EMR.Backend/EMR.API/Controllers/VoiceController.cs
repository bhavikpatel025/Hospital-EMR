using System;
using System.Threading.Tasks;
using EMR.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EMR.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class VoiceController : ControllerBase
{
    private readonly IVoiceService _voiceService;

    public VoiceController(IVoiceService voiceService)
    {
        _voiceService = voiceService;
    }

    [HttpPost("transcribe")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> TranscribeAudio(IFormFile audio)
    {
        if (audio == null || audio.Length == 0)
        {
            return BadRequest(new { message = "Audio file is required." });
        }

        try
        {
            var transcription = await _voiceService.TranscribeAudioAsync(audio);
            return Ok(new { text = transcription });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error during transcription.", error = ex.Message });
        }
    }
}
