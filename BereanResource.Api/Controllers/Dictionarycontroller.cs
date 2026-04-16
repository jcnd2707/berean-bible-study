using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/dictionary")]
public class DictionaryController(DictionaryService dictionaryService) : ControllerBase
{
    /// <summary>Looks up an entry by word/topic or Strong's number.</summary>
    [HttpGet("{moduleId}/lookup")]
    public IActionResult Lookup(
        string moduleId,
        [FromQuery] string? word = null,
        [FromQuery] string? strongs = null)
    {
        if (string.IsNullOrWhiteSpace(word) && string.IsNullOrWhiteSpace(strongs))
            return BadRequest(new { error = "Provide either 'word' or 'strongs' query parameter." });
        try
        {
            var result = strongs is not null
                ? dictionaryService.LookupByStrongs(moduleId, strongs)
                : dictionaryService.LookupByWord(moduleId, word!);

            return result is null
                ? NotFound(new { error = $"Entry not found in '{moduleId}'." })
                : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>Full-text search across topics in a module.</summary>
    [HttpGet("{moduleId}/search")]
    public IActionResult Search(
        string moduleId,
        [FromQuery] string q,
        [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Provide a search term via 'q'." });
        try
        {
            return Ok(dictionaryService.Search(moduleId, q, limit));
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>
    /// Returns metadata from the module's details table (MySword .dct only).
    /// Responds with 204 No Content for e-Sword modules that carry no metadata.
    /// </summary>
    [HttpGet("{moduleId}/details")]
    public IActionResult Details(string moduleId)
    {
        try
        {
            var result = dictionaryService.GetDetails(moduleId);
            return result is null ? NoContent() : Ok(result);
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    /// <summary>
    /// Returns a paginated list of entries in their natural order.
    /// Use <c>offset</c> and <c>pageSize</c> to walk through the module.
    /// </summary>
    [HttpGet("{moduleId}/entries")]
    public IActionResult Entries(
        string moduleId,
        [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 50)
    {
        if (offset < 0)
            return BadRequest(new { error = "'offset' must be >= 0." });
        if (pageSize is < 1 or > 500)
            return BadRequest(new { error = "'pageSize' must be between 1 and 500." });
        try
        {
            return Ok(dictionaryService.GetPage(moduleId, offset, pageSize));
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }
}