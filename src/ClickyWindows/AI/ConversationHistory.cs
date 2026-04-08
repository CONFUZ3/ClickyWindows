namespace ClickyWindows.AI;

/// <summary>
/// Stores text-only conversation history (no screenshots in history to prevent context bloat).
/// Screenshots are only sent with the current request.
/// </summary>
public class ConversationHistory
{
    public record Turn(string Role, string Content);

    private readonly List<Turn> _turns = new();
    private readonly int _maxTurns;

    public ConversationHistory(int maxTurns = 10)
    {
        _maxTurns = maxTurns;
    }

    public void AddUserMessage(string transcript)
    {
        _turns.Add(new Turn("user", transcript));
        Trim();
    }

    public void AddAssistantMessage(string response)
    {
        _turns.Add(new Turn("assistant", response));
        Trim();
    }

    public IReadOnlyList<Turn> GetHistory() => _turns.AsReadOnly();

    public void Clear() => _turns.Clear();

    private void Trim()
    {
        // Keep last maxTurns exchanges (2 turns per exchange)
        int maxMessages = _maxTurns * 2;
        while (_turns.Count > maxMessages)
            _turns.RemoveAt(0);
    }
}
