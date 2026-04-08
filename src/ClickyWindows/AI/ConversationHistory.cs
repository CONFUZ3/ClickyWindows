using System.IO;
using System.Text.Json;

namespace ClickyWindows.AI;

/// <summary>
/// Stores conversation history. In-memory turns may include a screenshot (clicky approach).
/// Disk persistence saves text only — screenshots are ephemeral.
/// </summary>
public class ConversationHistory
{
    public record Turn(string Role, string Content, string? ScreenshotBase64 = null);

    private readonly List<Turn> _turns = new();
    private readonly int _maxTurns;

    public ConversationHistory(int maxTurns = 10)
    {
        _maxTurns = maxTurns;
    }

    public void AddUserMessage(string transcript, string? screenshotBase64 = null)
    {
        _turns.Add(new Turn("user", transcript, screenshotBase64));
        Trim();
    }

    public void AddAssistantMessage(string response)
    {
        _turns.Add(new Turn("assistant", response));
        Trim();
    }

    public IReadOnlyList<Turn> GetHistory() => _turns.AsReadOnly();

    public void Clear() => _turns.Clear();

    /// <summary>Saves text-only turns to disk (no screenshots — they're large and stale).</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var textOnly = _turns.Select(t => new Turn(t.Role, t.Content)).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(textOnly));
        }
        catch { /* non-fatal — history will just not persist */ }
    }

    /// <summary>Loads text-only history from disk. Returns an empty history on any failure.</summary>
    public static ConversationHistory Load(string path, int maxTurns = 10)
    {
        var history = new ConversationHistory(maxTurns);
        if (!File.Exists(path)) return history;

        try
        {
            var turns = JsonSerializer.Deserialize<List<Turn>>(File.ReadAllText(path));
            if (turns != null)
                foreach (var t in turns)
                    history._turns.Add(t);
            history.Trim();
        }
        catch { /* corrupted file — start fresh */ }

        return history;
    }

    private void Trim()
    {
        // Keep last maxTurns exchanges (2 turns per exchange)
        int maxMessages = _maxTurns * 2;
        while (_turns.Count > maxMessages)
            _turns.RemoveAt(0);
    }
}
