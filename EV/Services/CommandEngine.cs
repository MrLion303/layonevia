namespace EV.Services;

public sealed class CommandEngine
{
    public bool TryGetMemoryRequest(string text, out string memory)
    {
        memory = "";
        var normalized = text.Trim();
        var prefixes = new[] { "recuerda que ", "recuerda ", "quiero que recuerdes que ", "necesito que recuerdes que " };

        foreach (var prefix in prefixes)
        {
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            memory = normalized[prefix.Length..].Trim();
            return memory.Length > 0;
        }

        return false;
    }
}