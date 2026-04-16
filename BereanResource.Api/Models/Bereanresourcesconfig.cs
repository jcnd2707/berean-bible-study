namespace BereanResourceApi.Models;

public class BereanResourcesConfig
{
    public const string SectionName = "BereanResources";

    public string RootPath { get; set; } = string.Empty;
    public string NotesDbPath { get; set; } = string.Empty;
    public string CrossReferencesDbFolder { get; set; } = string.Empty;
    public SubFolderConfig SubFolders { get; set; } = new();
}

public class SubFolderConfig
{
    public string Bibles { get; set; } = "Bibles";
    public string Commentaries { get; set; } = "Commentaries";
    public string Dictionaries { get; set; } = "Dictionaries";
    public string Lexicons { get; set; } = "Lexicons";
    public string TopicNotes { get; set; } = "TopicNotes";
}