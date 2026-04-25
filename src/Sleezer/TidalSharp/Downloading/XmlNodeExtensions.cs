using System.Xml;

namespace TidalSharp.Downloading;

internal static class XmlNodeExtensions
{
    public static T NodeToMPDType<T>(this XmlNode node) where T : IMPDNode, new()
    {
        var mpdNode = new T();
        mpdNode.Parse(node);
        return mpdNode;
    }

    public static T[] GetChildrenOfType<T>(this XmlNode node, string tag) where T : IMPDNode, new()
    {
        var children = node.ChildNodes.Cast<XmlNode>();
        var childNodesOfTag = children.Where(n => n.LocalName == tag).ToArray();
        var nodeArray = new T[childNodesOfTag.Length];
        for (int i = 0; i < childNodesOfTag.Length; i++)
        {
            var child = new T();
            child.Parse(childNodesOfTag[i]);
            nodeArray[i] = child;
        }

        return nodeArray;
    }

    public static T? GetAttributeValue<T>(this XmlNode node, string attributeName) where T : struct
    {
        var attribute = node.Attributes?[attributeName]?.Value;
        if (attribute == null)
            return null;

        if (typeof(T) == typeof(bool))
            return (T)(object)bool.Parse(attribute);
        if (typeof(T) == typeof(uint))
            return (T)(object)uint.Parse(attribute);
        if (typeof(T) == typeof(int))
            return (T)(object)int.Parse(attribute);
        if (typeof(T) == typeof(long))
            return (T)(object)long.Parse(attribute);
        if (typeof(T) == typeof(ulong))
            return (T)(object)ulong.Parse(attribute);
        if (typeof(T) == typeof(double))
            return (T)(object)double.Parse(attribute);
        if (typeof(T) == typeof(TimeSpan))
            return (T)(object)XmlConvert.ToTimeSpan(attribute);
        if (typeof(T) == typeof(DateTime))
            return (T)(object)DateTime.Parse(attribute);

        return null;
    }

    public static T? GetAttributeValue<T>(this XmlNode node, string attributeName, string? ignore = null) where T : class
    {
        var attribute = node.Attributes?[attributeName]?.Value;
        if (attribute == null)
            return null;

        if (typeof(T) == typeof(string))
            return (T)(object)attribute;

        return attribute as T;
    }
}