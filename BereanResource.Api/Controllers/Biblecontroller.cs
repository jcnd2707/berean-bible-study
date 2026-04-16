using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/bible")]
public class BibleController(BibleService bibleService) : ControllerBase
{
    /// <summary>Lists all books present in a Bible module.</summary>
    [HttpGet("{moduleId}/books")]
    public IActionResult GetBooks(string moduleId, [FromQuery] string lang = "en")
    {
        try
        {
            return Ok(bibleService.GetBooks(moduleId, lang));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>Returns all verses in a chapter.</summary>
    [HttpGet("{moduleId}/{book}/{chapter:int}")]
    public IActionResult GetChapter(string moduleId, string book, int chapter, [FromQuery] string lang = "en")
    {
        try
        {
            var result = bibleService.GetChapter(moduleId, book, chapter, lang);
            return result is null
                ? NotFound(new { error = $"Chapter not found: {book} {chapter}" })
                : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Returns a single verse.</summary>
    [HttpGet("{moduleId}/{book}/{chapter:int}/{verse:int}")]
    public IActionResult GetVerse(string moduleId, string book, int chapter, int verse, [FromQuery] string lang = "en")
    {
        try
        {
            var result = bibleService.GetVerse(moduleId, book, chapter, verse, lang);
            return result is null
                ? NotFound(new { error = $"Verse not found: {book} {chapter}:{verse}" })
                : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}