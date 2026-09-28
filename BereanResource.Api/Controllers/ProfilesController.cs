using BereanResourceApi.Models;
using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/profiles")]
public class ProfilesController(ProfileService profiles, NotesService notes) : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(profiles.List());

    [HttpGet("unowned")]
    public IActionResult UnownedCount() => Ok(new { notes = notes.CountUnowned() });

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var profile = profiles.Get(id);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPost]
    public IActionResult Create([FromBody] CreateProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "Name cannot be empty." });

        return Ok(profiles.Create(request.Name.Trim(), request.Color));
    }

    [HttpPut("{id}")]
    public IActionResult Rename(string id, [FromBody] RenameProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "Name cannot be empty." });

        var updated = profiles.Rename(id, request.Name.Trim());
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>
    /// Moves every note written before profiles existed (ProfileId = '') to this profile.
    /// Uses UPDATE OR IGNORE, so a note this profile already has at the same reference is left
    /// unowned rather than overwritten or lost.
    /// </summary>
    [HttpPost("{id}/adopt-unowned")]
    public IActionResult AdoptUnowned(string id)
    {
        if (profiles.Get(id) is null) return NotFound();

        var moved = notes.AdoptUnowned(id);
        return Ok(new { moved, remaining = notes.CountUnowned() });
    }
}
