namespace Lms.Api.Infrastructure.Security;

public interface IManagedSecretStore
{
    string? Get(string reference);
}

public sealed class ConfigurationSecretStore(IConfiguration configuration) : IManagedSecretStore
{
    public string? Get(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var prefix = configuration["SecretStore:Prefix"] ?? "LMS_SECRET__";
        return configuration[reference]
            ?? configuration[$"Secrets:{reference}"]
            ?? Environment.GetEnvironmentVariable(reference)
            ?? Environment.GetEnvironmentVariable($"{prefix}{reference}");
    }
}
