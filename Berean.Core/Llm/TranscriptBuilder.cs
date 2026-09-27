using System.Text;
using Microsoft.Extensions.AI;

namespace Berean.Core.Llm;

/// <summary>
/// Turns earlier messages into a short transcript for a fresh session. Used when a Claude Code
/// session can't be resumed (it expired, the history was trimmed, or a saved conversation is
/// reopened): the last few exchanges go in full, older ones as one line each.
/// </summary>
public static class TranscriptBuilder
{
    /// <summary>Message property holding the user's own text, without the retrieved material.</summary>
    public const string QuestionProperty = "berean.question";

    private const int OlderQuestionChars = 160;
    private const int OlderAnswerChars = 280;

    public static string Condense(IEnumerable<ChatMessage> prior, int fullTurns = 4, int maxChars = 16000)
    {
        var turns = ToTurns(prior);
        if (turns.Count == 0) return "";

        var older = turns.Take(Math.Max(0, turns.Count - fullTurns)).ToList();
        var recent = turns.Skip(older.Count).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("[Earlier in this conversation — for context only]");
        sb.AppendLine();

        if (older.Count > 0)
        {
            sb.AppendLine("Earlier exchanges, in brief:");
            foreach (var t in older)
                sb.AppendLine($"- Q: {Clip(t.Question, OlderQuestionChars)} → A: {Clip(t.Answer, OlderAnswerChars)}");
            sb.AppendLine();
        }

        foreach (var t in recent)
        {
            sb.AppendLine($"User: {t.Question}");
            sb.AppendLine($"Assistant: {t.Answer}");
            sb.AppendLine();
        }

        var text = sb.ToString().TrimEnd();
        // Over budget: drop the oldest of the recent turns' detail rather than the newest.
        return text.Length <= maxChars ? text : "[…]\n" + text[^maxChars..];
    }

    private sealed record Turn(string Question, string Answer);

    private static List<Turn> ToTurns(IEnumerable<ChatMessage> messages)
    {
        var turns = new List<Turn>();
        string? question = null;
        var answer = new StringBuilder();

        void Flush()
        {
            if (question is not null && answer.Length > 0)
                turns.Add(new Turn(question, answer.ToString().Trim()));
            question = null;
            answer.Clear();
        }

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.User)
            {
                Flush();
                // Prefer the plain question: the message itself carries retrieved material.
                question = m.AdditionalProperties?.TryGetValue(QuestionProperty, out var q) == true && q is string s
                    ? s : m.Text;
            }
            else if (m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
            {
                answer.AppendLine(m.Text);
            }
        }
        Flush();
        return turns;
    }

    private static string Clip(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "…";
    }
}
