using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/crossreferences")]
public class CrossReferencesController(CrossReferenceService crossReferenceService) : ControllerBase
{
    /// <summary>
    /// Returns cross-references for a specific verse, ordered by relevance (votes).
    /// Example: GET /api/crossreferences/John/3/16
    /// </summary>
    [HttpGet("{book}/{chapter:int}/{verse:int}")]
    public IActionResult GetForVerse(
        string book,
        int chapter,
        int verse,
        [FromQuery] int minVotes = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string lang = "en")
    {
        try
        {
            var result = crossReferenceService.GetForVerse(book, chapter, verse, minVotes, limit, lang);
            return result is null
                ? NotFound(new { error = "Cross-references database not available." })
                : Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}