using System.Collections.Generic;

namespace Microwalk.FrameworkBase.Configuration;

public class ListNode : Node
{
    public ListNode(List<Node> children)
    {
        Children = children;
    }

    public List<Node> Children { get; }
}