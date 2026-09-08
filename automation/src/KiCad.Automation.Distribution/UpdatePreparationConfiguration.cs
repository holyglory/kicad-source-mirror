namespace KiCad.Automation.Distribution;

public sealed record UpdatePreparationConfiguration(int SchemaVersion, string Origin, string PublisherKeySpki,
    string InstalledEnvelope, string StateDirectory, string StagingDirectory, string Channel, string Platform, string Format,
    string? InstallationRoot = null);
