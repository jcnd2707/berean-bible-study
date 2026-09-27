using BereanResourceApi.Models;
using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/resources")]
public class ResourcesController(
    ResourceDiscoveryService discovery,
    BookService bookService,
    ModuleProfileService profiles) : ControllerBase
{
    [HttpGet("bibles")]
    public IActionResult GetBibles() => Ok(discovery.GetBibles());

    [HttpGet("commentaries")]
    public IActionResult GetCommentaries() => Ok(profiles.Apply(discovery.GetCommentaries()));

    [HttpGet("dictionaries")]
    public IActionResult GetDictionaries() => Ok(profiles.Apply(discovery.GetDictionaries()));

    [HttpGet("lexicons")]
    public IActionResult GetLexicons() => Ok(profiles.Apply(discovery.GetLexicons()));

    [HttpGet("topic-notes")]
    public IActionResult GetTopicNotes() => Ok(discovery.GetTopicNotes());

    [HttpGet("books")]
    public IActionResult GetBooks() => Ok(profiles.Apply(bookService.GetAvailableBooks()));

    /// <summary>
    /// Every commentary, book, dictionary and lexicon that has no entry under "ModuleProfiles".
    /// This is the list to label (add a tradition and era for each in appsettings.json).
    /// </summary>
    [HttpGet("profiles/unclassified")]
    public IActionResult GetUnclassified()
    {
        var result = new List<UnclassifiedModule>();
        result.AddRange(discovery.GetCommentaries().Where(m => !profiles.HasProfile(m.ModuleId))
            .Select(m => new UnclassifiedModule("commentary", m.ModuleId, m.Name)));
        result.AddRange(bookService.GetAvailableBooks().Where(b => !profiles.HasProfile(b.ModuleId))
            .Select(b => new UnclassifiedModule("book", b.ModuleId, b.Title)));
        result.AddRange(discovery.GetDictionaries().Where(m => !profiles.HasProfile(m.ModuleId))
            .Select(m => new UnclassifiedModule("dictionary", m.ModuleId, m.Name)));
        result.AddRange(discovery.GetLexicons().Where(m => !profiles.HasProfile(m.ModuleId))
            .Select(m => new UnclassifiedModule("lexicon", m.ModuleId, m.Name)));
        return Ok(result);
    }
}
