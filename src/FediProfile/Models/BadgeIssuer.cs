namespace FediProfile.Models;

public class BadgeIssuer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ActorUrl { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string? Bio { get; set; }
    public bool Following { get; set; }
    public string CreatedUtc { get; set; } = string.Empty;
}
