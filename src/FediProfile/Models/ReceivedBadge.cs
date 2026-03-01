namespace FediProfile.Models;

public class ReceivedBadge
{
    public int Id { get; set; }
    public string NoteId { get; set; } = string.Empty;
    public int IssuerId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string? Description { get; set; }
    public string? IssuedOn { get; set; }
    public string? AcceptedOn { get; set; }
    public string ReceivedUtc { get; set; } = string.Empty;
}
