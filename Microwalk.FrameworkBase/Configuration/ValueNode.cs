namespace Microwalk.FrameworkBase.Configuration;

public class ValueNode : Node
{
    public ValueNode(string? value)
    {
        Value = value;
    }

    public string? Value { get; set; }
}