namespace Berean.Core.Agent;

public static class StudyAgentFactory
{
    /// <summary>
    /// Creates the study agent's prompts and tool suite on top of the shared library.
    /// Providers that run without tools (Claude Code) never see the tools; the router
    /// pre-fetches the same information for them.
    /// </summary>
    public static (StudyAgentOptions options, ToolRegistry tools) Create(BibleKnowledge knowledge)
    {
        var options = new StudyAgentOptions
        {
            SystemPrompt = Prompts.System,
            PerspectiveAddendumTemplate = Prompts.PerspectiveAddendum,
            CompareInstructions = Prompts.CompareAddendum,
        };

        var tools = new ToolRegistry();
        tools.Register(new LookupWordTool(knowledge).RunAsync, LookupWordTool.Name);
        tools.Register(new FindWordOccurrencesTool(knowledge).RunAsync, FindWordOccurrencesTool.Name);
        tools.Register(new CrossReferencesTool(knowledge).RunAsync, CrossReferencesTool.Name);
        tools.Register(new LookupVerseTool(knowledge).RunAsync, LookupVerseTool.Name);
        tools.Register(new GetPassageTool(knowledge).RunAsync, GetPassageTool.Name);
        return (options, tools);
    }
}
