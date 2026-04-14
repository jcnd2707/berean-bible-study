
namespace HybridAgent.Core.RAG 
{
    /// The pipeline scans ModulesRootPath recursively and processes every file
    /// whose extension is in AllowedExtensions. No wildcards or per-file lists needed.
    /// </summary>
    public class AgentRagConfig
    {
        /// <summary>
        /// Path to the SQLite RAG database written by this application.
        /// Relative paths resolve from the API working directory.
        /// </summary>
        public string RagDbPath { get; set; } = "index/agent.rag.db";

        /// <summary>
        /// Root folder that is scanned recursively for source files.
        /// </summary>
        public string ModulesRootPath { get; set; } = string.Empty;

        /// <summary>
        /// File extensions to include, e.g. ".bblx", ".txt", ".md", ".cs".
        /// The leading dot is required. Comparison is case-insensitive.
        /// </summary>
        public List<string> AllowedExtensions { get; set; } = [];

        /// <summary>
        /// Returns all file paths under ModulesRootPath that match AllowedExtensions.
        /// Returns an empty list if the directory does not exist.
        /// </summary>
        public IEnumerable<string> ResolveFiles()
        {
            if (string.IsNullOrWhiteSpace(ModulesRootPath) || !Directory.Exists(ModulesRootPath))
                return [];

            var extensions = AllowedExtensions
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
                .ToHashSet();

            if (extensions.Count == 0)
                return [];

            return Directory
                .EnumerateFiles(ModulesRootPath, "*.*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(
                    Path.GetExtension(f).ToLowerInvariant()));
        }
    }
}