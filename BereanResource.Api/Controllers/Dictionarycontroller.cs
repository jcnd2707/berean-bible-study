using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.Design;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/dictionary")]
public class DictionaryController(DictionaryService dictionaryService) : ControllerBase
{
    /// <summary>Looks up an entry by word/topic.</summary>
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
    public IActionResult Search(string moduleId, [FromQuery] string q, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Provide a search term via 'q'." });

        try
        {
            return Ok(dictionaryService.Search(moduleId, q, limit));
        }
        catch (FileNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }
}