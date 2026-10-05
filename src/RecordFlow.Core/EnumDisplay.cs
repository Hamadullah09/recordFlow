using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace RecordFlow.Core;

public static class EnumDisplay
{
    private static readonly ConcurrentDictionary<Enum, string> Cache = new();

    /// <summary>Returns the [Display(Name)] of an enum value, or its name split into words.</summary>
    public static string Name(this Enum value) => Cache.GetOrAdd(value, v =>
    {
        var member = v.GetType().GetMember(v.ToString()).FirstOrDefault();
        var display = member?.GetCustomAttribute<DisplayAttribute>()?.GetName();
        return display ?? SplitWords(v.ToString());
    });

    private static string SplitWords(string s) =>
        string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
}
