namespace FediProfile.Models;

public class Link
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool AutoBoost { get; set; }
    public bool IsActivityPub { get; set; }
    public string? Category { get; set; }
    public string? Type { get; set; }
    public bool Following { get; set; }
    public bool Hidden { get; set; }
    public string? ActorAPUri { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
}
