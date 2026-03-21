using System.Text.Json;

namespace FediProfile.Core;

/// <summary>
/// Shared logic for extracting OpenBadge assertion data from ActivityPub Note objects.
/// Used by both the manual import flow (AdminIndex) and the background job processor.
/// </summary>
public static class BadgeParser
{
    /// <summary>
    /// Represents a single parsed OpenBadge assertion extracted from a Note attachment.
    /// </summary>
    public record ParsedAssertion(
        string RecipientUri,
        string BadgeTitle,
        string? BadgeImage,
        string? BadgeDescription,
        string? IssuedOn,
        JsonElement RawAttachment);

    /// <summary>
    /// Extracts all valid OpenBadge Assertion attachments from a Note's JSON-LD root element.
    /// Each assertion must have type "Assertion" and a recipient with an identity URI.
    /// Badge details (name, image, description) are read from the nested "badge" (BadgeClass) object.
    /// </summary>
    public static List<ParsedAssertion> ExtractAssertions(JsonElement noteRoot)
    {
        var results = new List<ParsedAssertion>();

        if (!noteRoot.TryGetProperty("attachment", out var attachments) ||
            attachments.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var attachment in attachments.EnumerateArray())
        {
            if (!attachment.TryGetProperty("type", out var typeEl) ||
                !string.Equals(typeEl.GetString(), "Assertion", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!attachment.TryGetProperty("recipient", out var recipientEl) ||
                !recipientEl.TryGetProperty("identity", out var identityEl))
                continue;

            var recipientIdentity = identityEl.GetString();
            if (string.IsNullOrEmpty(recipientIdentity))
                continue;

            // Extract badge details from the assertion's nested badge (BadgeClass) object
            var badgeTitle = "Unknown Badge";
            string? badgeImage = null;
            string? badgeDescription = null;

            if (attachment.TryGetProperty("badge", out var badgeEl) && badgeEl.ValueKind == JsonValueKind.Object)
            {
                badgeTitle = badgeEl.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString() ?? "Unknown Badge" : "Unknown Badge";
                badgeImage = badgeEl.TryGetProperty("image", out var imageEl)
                    ? imageEl.GetString() : null;
                badgeDescription = badgeEl.TryGetProperty("description", out var descEl)
                    ? descEl.GetString() : null;
            }

            var issuedOn = attachment.TryGetProperty("issuedOn", out var issuedEl)
                ? issuedEl.GetString() : null;

            results.Add(new ParsedAssertion(
                recipientIdentity, badgeTitle, badgeImage, badgeDescription, issuedOn, attachment));
        }

        return results;
    }
}
