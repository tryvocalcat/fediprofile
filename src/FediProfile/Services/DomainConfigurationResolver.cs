namespace FediProfile.Services;

using Microsoft.Extensions.Configuration;

public sealed class DomainConfigurationResolver
{
    private readonly IConfiguration _configuration;

    public DomainConfigurationResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public DomainRegistrationSettings GetRegistrationSettings(string? domain)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var singleUserInstance = _configuration.GetValue<bool>("SingleUserInstance", false);
        var registrationOpen = _configuration.GetValue<bool>("RegistrationOpen", true);

        Console.WriteLine(normalizedDomain);
        
        if (string.IsNullOrWhiteSpace(normalizedDomain))
        {
            return new DomainRegistrationSettings(singleUserInstance, registrationOpen);
        }

        var domainSection = _configuration.GetSection("DomainConfig").GetSection(normalizedDomain);
        if (domainSection.Exists())
        {
            Console.WriteLine("EXISTS");
            var domainSingleUser = domainSection.GetValue<bool?>("SingleUserInstance");
            var domainRegistrationOpen = domainSection.GetValue<bool?>("RegistrationOpen");

            Console.WriteLine(domainSingleUser);
            return new DomainRegistrationSettings(
                domainSingleUser ?? singleUserInstance,
                domainRegistrationOpen ?? registrationOpen);
        } else
        {
            Console.WriteLine("NOT EXISTS");
        }

        return new DomainRegistrationSettings(singleUserInstance, registrationOpen);
    }

    public static string? NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        return domain.Trim().ToLowerInvariant().TrimEnd('/').Replace('.', '_');
    }
}

public sealed record DomainRegistrationSettings(bool SingleUserInstance, bool RegistrationOpen);