using BereanResourceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace BereanResourceApi.Controllers;

[ApiController]
[Route("api/resources")]
public class ResourcesController(ResourceDiscoveryService discovery, BookService bookService) : ControllerBase
{
    [HttpGet("bibles")]
    public IActionResult GetBibles() => Ok(discovery.GetBibles());

    [HttpGet("commentaries")]
    public IActionResult GetCommentaries() => Ok(discovery.GetCommentaries());

    [HttpGet("dictionaries")]
    public IActionResult GetDictionaries() => Ok(discovery.GetDictionaries());

    [HttpGet("lexicons")]
    public IActionResult GetLexicons() => Ok(discovery.GetLexicons());

    [HttpGet("topic-notes")]
    public IActionResult GetTopicNotes() => Ok(discovery.GetTopicNotes());

    [HttpGet("books")]
    public IActionResult GetBooks() => Ok(bookService.GetAvailableBooks());
}