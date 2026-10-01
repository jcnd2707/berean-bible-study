using BereanResourceApi.Profiles;
using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/progress")]
[RequireProfile]
public class ProgressController(ReadingProgressService progress) : ControllerBase
{
    /// <summary>Every chapter this profile has marked read, as canonical (book 1-66, chapter) pairs.</summary>
    [HttpGet("chapters")]
    public IActionResult GetAll() => Ok(progress.GetAll(HttpContext.ProfileId()));

    /// <summary>Marks a chapter read, e.g. PUT /api/progress/chapters/1/1 for Genesis 1.</summary>
    [HttpPut("chapters/{book:int}/{chapter:int}")]
    public IActionResult MarkRead(int book, int chapter)
    {
        if (!ReadingProgressService.IsValidChapter(book, chapter))
            return NotFound(new { error = $"No such chapter: book {book}, chapter {chapter}." });

        return Ok(progress.MarkRead(HttpContext.ProfileId(), book, chapter));
    }

    /// <summary>Unmarks a chapter. 204 when it was marked, 404 when it wasn't.</summary>
    [HttpDelete("chapters/{book:int}/{chapter:int}")]
    public IActionResult MarkUnread(int book, int chapter) =>
        progress.MarkUnread(HttpContext.ProfileId(), book, chapter) ? NoContent() : NotFound();
}
