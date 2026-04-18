using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/books")]
public class BooksController(BookService bookService) : ControllerBase
{
    /// <summary>Lists all available book modules.</summary>
    [HttpGet]
    public IActionResult GetAvailableBooks() => Ok(bookService.GetAvailableBooks());

    /// <summary>Returns metadata (title, author, publisher, language) for a book module.</summary>
    [HttpGet("{moduleId}")]
    public IActionResult GetMeta(string moduleId)
    {
        var result = bookService.GetMeta(moduleId);
        return result is null
            ? NotFound(new { error = $"Book module '{moduleId}' not found." })
            : Ok(result);
    }

    /// <summary>Lists all chapters in a book module in reading order.</summary>
    [HttpGet("{moduleId}/chapters")]
    public IActionResult GetChapters(string moduleId)
    {
        var chapters = bookService.GetChapters(moduleId);
        return chapters.Count == 0
            ? NotFound(new { error = $"No chapters found for '{moduleId}'." })
            : Ok(chapters);
    }

    /// <summary>Returns a chapter's full content (paragraphs with HTML and plain text).</summary>
    [HttpGet("{moduleId}/chapters/{chapterId:int}")]
    public IActionResult GetChapter(string moduleId, int chapterId)
    {
        var result = bookService.GetChapter(moduleId, chapterId);
        return result is null
            ? NotFound(new { error = $"Chapter {chapterId} not found in '{moduleId}'." })
            : Ok(result);
    }

    /// <summary>Full-text search across all paragraphs in a book module.</summary>
    [HttpGet("{moduleId}/search")]
    public IActionResult Search(
        string moduleId, [FromQuery] string q, [FromQuery] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required." });

        return Ok(bookService.Search(moduleId, q, limit));
    }
}
