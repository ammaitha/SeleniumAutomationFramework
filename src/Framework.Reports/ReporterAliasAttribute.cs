namespace Framework.Reports;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ReporterAliasAttribute : Attribute
{
    public ReporterAliasAttribute(string name)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Reporter alias cannot be empty.", nameof(name))
            : name.Trim();
    }

    public string Name { get; }
}
