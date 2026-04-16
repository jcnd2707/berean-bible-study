using BereanResourceApi.Models;
using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/notes")]
public class NotesController(NotesService notesService) : ControllerBase
{
    /// <summary>Returns all notes ordered by reference.</summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(notesService.GetAll());

    /// <summary>
    /// Returns the note for a specific reference e.g. GET /api/notes/Gen.1.1
    /// The catch-all constraint {reference:regex(.*)} ensures dotted segments
    /// like "Gen.1.1" are not stripped by ASP.NET Core's static-file middleware.
    /// </summary>
    [HttpGet("{**reference}")]
    public IActionResult Get(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "Reference cannot be empty." });

        var note = notesService.Get(reference);
        return note is null ? NotFound() : Ok(note);
    }

    /// <summary>
    /// Creates or updates a note for a reference e.g. POST /api/notes/Gen.1.1
    /// The catch-all constraint keeps dotted references intact.
    /// </summary>
    [HttpPost("{**reference}")]
    public IActionResult Upsert(string reference, [FromBody] UpsertNoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "Reference cannot be empty." });

        if (string.IsNullOrWhiteSpace(request?.Text))
            return BadRequest(new { error = "Note text cannot be empty." });

        var note = notesService.Upsert(reference, request.Text);
        return Ok(note);
    }

    /// <summary>
    /// Deletes the note for a reference e.g. DELETE /api/notes/Gen.1.1
    /// </summary>
    [HttpDelete("{**reference}")]
    public IActionResult Delete(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "Reference cannot be empty." });

        var deleted = notesService.Delete(reference);
        return deleted ? NoContent() : NotFound();
    }
}