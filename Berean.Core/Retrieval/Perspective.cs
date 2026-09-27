namespace Berean.Core.Retrieval;

/// <summary>
/// One opt-in, tradition-specific pass, configured under "Perspectives" in appsettings.json.
/// The Adventist toggle used to be the only one of these; any tradition can now be set up this
/// way (see <see cref="Traditions"/> for the fixed vocabulary a perspective's tradition must use).
/// </summary>
/// <param name="Id">Stable identifier the client sends back ("adventist").</param>
/// <param name="Tradition">Which tradition's material this perspective draws on and holds out of the neutral pass.</param>
/// <param name="Label">Shown in the UI and used to head the sources block ("Seventh-day Adventist").</param>
/// <param name="CitationPrefix">Citation id prefix in the prompt ("ADV" → [ADV1], [ADV2]…).</param>
/// <param name="TopK">Chunks this perspective's own retrieval pass returns.</param>
public record Perspective(string Id, string Tradition, string Label, string CitationPrefix, int TopK = 4);
