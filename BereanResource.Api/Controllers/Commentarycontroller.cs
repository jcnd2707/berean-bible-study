using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/commentary")]
public class CommentaryController(CommentaryService commentaryService) : ControllerBase
{
    /// <summary>Returns commentary for a full chapter.</summary>
    [HttpGet("{moduleId}/{book}/{chapter:int}")]
    public IActionResult GetChapter(string moduleId, string book, int chapter, [FromQuery] string lang = "en")
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
    public IActionResult GetVerse(string moduleId, string book, int chapter, int verse, [FromQuery] string lang = "en")
    {
        try
        {
            var result = commentaryService.GetVerse(moduleId, book, chapter, verse, lang);
            return Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}