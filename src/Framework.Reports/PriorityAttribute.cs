namespace Framework.Reports;

public enum TestPriority
{
    High = 1,
    Medium = 2,
    Low = 3
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PriorityAttribute : Attribute
{
    public TestPriority Level { get; }

    public PriorityAttribute(TestPriority level)
    {
        if (!Enum.IsDefined(typeof(TestPriority), level))
        {
            throw new ArgumentException("Invalid priority level");
        }

        Level = level;
    }
}
