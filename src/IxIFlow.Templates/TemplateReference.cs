namespace IxIFlow.Templates;

/// <summary>
/// A template a step renders: its key and the exact version the workflow definition pins. A definition names
/// the version, so a newer version of the template never changes what an instance of that definition renders, and a
/// recovered step renders the version its first attempt saved with its inputs.
/// </summary>
public sealed record TemplateReference
{
    public TemplateReference(string key, int version)
    {
        TemplateVersion.CheckKey(key);
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "Template versions start at 1");
        Key = key;
        Version = version;
    }

    public string Key { get; }

    public int Version { get; }

    public override string ToString() => $"{Key} v{Version}";
}
