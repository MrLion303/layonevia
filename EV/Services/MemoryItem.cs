namespace EV.Services;

public sealed class MemoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public string Category { get; set; } = "general";
    public string? Subject { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}