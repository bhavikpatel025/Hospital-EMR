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

    [HttpPost("extract")]
    public async Task<IActionResult> ExtractVoiceData([FromBody] ExtractRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Text))
        {
            return BadRequest(new { message = "Text is required for extraction." });
        }

        try
        {
            var extractedData = await _voiceService.ExtractVoiceDataAsync(request.Text);
            return Ok(extractedData);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error during data extraction.", error = ex.Message });
        }
    }
}

public class ExtractRequest
{
    public string Text { get; set; } = string.Empty;
}
