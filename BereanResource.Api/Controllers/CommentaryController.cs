using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/commentary")]
public class CommentaryController(CommentaryService commentaryService) : ControllerBase
{
    // ── Details ───────────────────────────────────────────────────────────────

    /// <summary>Returns metadata for a single commentary (or group).</summary>
    [HttpGet("{moduleId}/details")]
    public IActionResult GetDetails(string moduleId)
    {
        try
        {
            var result = commentaryService.GetInfo(moduleId);
            return result is null
                ? NotFound(new { error = $"Commentary '{moduleId}' not found." })
                : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    // ── Content ───────────────────────────────────────────────────────────────

    /// <summary>Returns commentary for a full chapter.</summary>
    [HttpGet("{moduleId}/{book}/{chapter:int}")]
    public IActionResult GetChapter(
        string moduleId, string book, int chapter, [FromQuery] string lang = "en")
    {
        try
        {
            var result = commentaryService.GetChapter(moduleId, book, chapter, lang);
            return result is null
                ? NotFound(new { error = $"No commentary found: {book} {chapter}" })
                : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Returns commentary entries that cover a specific verse.</summary>
    [HttpGet("{moduleId}/{book}/{chapter:int}/{verse:int}")]
    public IActionResult GetVerse(
        string moduleId, string book, int chapter, int verse, [FromQuery] string lang = "en")
    {
        try
        {
            var result = commentaryService.GetVerse(moduleId, book, chapter, verse, lang);
            return Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Returns the book-level introduction entry, if present.</summary>
    [HttpGet("{moduleId}/{book}/intro")]
    public IActionResult GetBookIntro(
        string moduleId, string book, [FromQuery] string lang = "en")
    {
        try
        {
            var result = commentaryService.GetBookIntro(moduleId, book, lang);
            return result is null
                ? NotFound(new { error = $"No intro found for {book} in '{moduleId}'." })
                : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}