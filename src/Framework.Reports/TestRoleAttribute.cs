using NUnit.Framework;

namespace Framework.Reports;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class TestRoleAttribute : CategoryAttribute
{
    public string Role => Name;

    public TestRoleAttribute(string role) : base(Normalize(role))
    {
    }

    private static string Normalize(string role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("Role name is required.", nameof(role));
        }

        return role.Trim().ToLowerInvariant();
    }
}
