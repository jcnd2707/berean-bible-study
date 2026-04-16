using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/bible")]
public class BibleController(BibleService bibleService) : ControllerBase
{
    /// <summary>Returns metadata for a Bible module (title, license, translation code).</summary>
    [HttpGet("{moduleId}")]
    public IActionResult GetTranslationInfo(string moduleId)
    {
        try
        {
            var result = bibleService.GetTranslationInfo(moduleId);
            return result is null
                ? NotFound(new { error = $"Module '{moduleId}' has no translation metadata." })
                : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Lists all books present in a Bible module.</summary>
    [HttpGet("{moduleId}/books")]
    public IActionResult GetBooks(string moduleId, [FromQuery] string lang = "en")
    {
        try
        {
            return Ok(bibleService.GetBooks(moduleId, lang));
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
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

    /// <summary>
    /// Full-text search across the Bible module.
    /// GET /api/bible/{moduleId}/search?q=living+water&amp;testament=NT&amp;limit=100&amp;book=Rom
    /// </summary>
    [HttpGet("{moduleId}/search")]
    public IActionResult Search(
        string moduleId,
        [FromQuery] string? q,
        [FromQuery] int limit = 50,
        [FromQuery] string testament = "both",
        [FromQuery] string? book = null)
    {
        // Validate query string
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new { error = "Query parameter 'q' is required and must be at least 2 characters." });

        // Validate testament filter
        var testamentNorm = testament.ToUpperInvariant();
        if (testamentNorm != "OT" && testamentNorm != "NT" && testamentNorm != "BOTH")
            return BadRequest(new { error = "Parameter 'testament' must be 'OT', 'NT', or 'both'." });

        // Cap limit
        limit = Math.Clamp(limit, 1, 200);

        try
        {
            var results = bibleService.Search(moduleId, q.Trim(), limit, testamentNorm, book);
            return Ok(results);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}