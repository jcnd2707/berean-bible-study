using BereanResourceApi.Models;
using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/notes")]
public class NotesController(NotesService notesService) : ControllerBase
{
    /// <summary>Returns all notes.</summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(notesService.GetAll());

    /// <summary>Returns the note for a specific reference e.g. GET /api/notes/John 3:16</summary>
    [HttpGet("{reference}")]
    public IActionResult Get(string reference)
    {
        var note = notesService.Get(reference);
        return note is null ? NotFound() : Ok(note);
    }

    /// <summary>Creates or updates a note for a reference.</summary>
    [HttpPost("{reference}")]
    public IActionResult Upsert(string reference, [FromBody] UpsertNoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "Note text cannot be empty." });

        var note = notesService.Upsert(reference, request.Text);
        return Ok(note);
    }

    /// <summary>Deletes a note.</summary>
    [HttpDelete("{reference}")]
    public IActionResult Delete(string reference)
    {
        var deleted = notesService.Delete(reference);
        return deleted ? NoContent() : NotFound();
    }
}