using BereanResourceApi.Models;
using BereanResourceApi.Profiles;
using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/notes")]
[RequireProfile]
public class NotesController(NotesService notesService) : ControllerBase
{
    /// <summary>Returns all of this profile's notes ordered by reference.</summary>
    [HttpGet]
    public IActionResult GetAll() => Ok(notesService.GetAll(HttpContext.ProfileId()));

    /// <summary>
    /// Returns this profile's note for a specific reference e.g. GET /api/notes/Gen.1.1
    /// The catch-all constraint {reference:regex(.*)} ensures dotted segments
    /// like "Gen.1.1" are not stripped by ASP.NET Core's static-file middleware.
    /// </summary>
    [HttpGet("{**reference}")]
    public IActionResult Get(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "Reference cannot be empty." });

        var note = notesService.Get(HttpContext.ProfileId(), reference);
        return note is null ? NotFound() : Ok(note);
    }

    /// <summary>
    /// Creates or updates this profile's note for a reference e.g. POST /api/notes/Gen.1.1
    /// The catch-all constraint keeps dotted references intact.
    /// </summary>
    [HttpPost("{**reference}")]
    public IActionResult Upsert(string reference, [FromBody] UpsertNoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "Reference cannot be empty." });

        if (string.IsNullOrWhiteSpace(request?.Text))
            return BadRequest(new { error = "Note text cannot be empty." });

        var note = notesService.Upsert(HttpContext.ProfileId(), reference, request.Text);
        return Ok(note);
    }

    /// <summary>
    /// Adds text to the end of this profile's note for a reference without replacing it,
    /// e.g. POST /api/notes/Gen.1.1/append. Creates the note if there is none.
    /// </summary>
    [HttpPost("{reference}/append")]
    public IActionResult Append(string reference, [FromBody] UpsertNoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "Reference cannot be empty." });

        if (string.IsNullOrWhiteSpace(request?.Text))
            return BadRequest(new { error = "Note text cannot be empty." });

        return Ok(notesService.Append(HttpContext.ProfileId(), reference, request.Text));
    }

    /// <summary>
    /// Deletes this profile's note for a reference e.g. DELETE /api/notes/Gen.1.1
    /// </summary>
    [HttpDelete("{**reference}")]
    public IActionResult Delete(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return BadRequest(new { error = "Reference cannot be empty." });

        var deleted = notesService.Delete(HttpContext.ProfileId(), reference);
        return deleted ? NoContent() : NotFound();
    }
}
