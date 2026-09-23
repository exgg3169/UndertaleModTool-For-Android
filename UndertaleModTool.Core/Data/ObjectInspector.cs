using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace UndertaleModTool.Core.Data;

/// <summary>
/// Reflection helpers to look up, describe (as JSON) and edit UndertaleModLib objects by path.
/// </summary>
public static class ObjectInspector
{
    #region Lookup

    public static IEnumerable<PropertyInfo> CategoryProperties(UndertaleData data)
        => data.AllListProperties.Where(p => p.GetValue(data) is IList);

    public static IEnumerable<string> CategoryNames(UndertaleData data) => CategoryProperties(data).Select(p => p.Name);

    /// <summary>Returns a resource list by name (e.g. "Sprites", "code", "Strings").</summary>
    public static IList GetCategory(UndertaleData data, string name)
    {
        PropertyInfo property = CategoryProperties(data).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown category \"{name}\". Categories: {string.Join(", ", CategoryNames(data))}");
        return (IList)property.GetValue(data);
    }

    /// <summary>Display name of an object: resource name, string content, or ToString().</summary>
    public static string NameOf(object obj)
    {
        obj = Unwrap(obj);
        try
        {
            return obj switch
            {
                null => null,
                UndertaleString s => s.Content,
                UndertaleNamedResource named => named.Name?.Content,
                _ => obj.ToString(),
            };
        }
        catch
        {
            return obj.GetType().Name;
        }
    }

    /// <summary>Resolves resource-by-id wrappers to their resource.</summary>
    public static object Unwrap(object obj)
        => obj is UndertaleResourceRef ? obj.GetType().GetProperty("Resource")?.GetValue(obj) : obj;

    /// <summary>
    /// Finds an item in a list by index, or by name (exact, then case-insensitive).
    /// </summary>
    public static object FindItem(IList list, string name, int? index, out int foundIndex)
    {
        if (index is int i)
        {
            if (i < 0 || i >= list.Count)
                throw new ArgumentException($"Index {i} is out of range (0..{list.Count - 1}).");
            foundIndex = i;
            return list[i];
        }
        if (name is null)
            throw new ArgumentException("Specify either \"name\" or \"index\".");
        for (int pass = 0; pass < 2; pass++)
        {
            StringComparison cmp = pass == 0 ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int j = 0; j < list.Count; j++)
            {
                if (string.Equals(NameOf(list[j]), name, cmp))
                {
                    foundIndex = j;
                    return list[j];
                }
            }
        }
        throw new ArgumentException($"No item named \"{name}\" in this list.");
    }

    /// <summary>Finds a named resource of a given type anywhere in the data (e.g. to assign references).</summary>
    public static object FindResourceOfType(UndertaleData data, Type type, string name)
    {
        foreach (PropertyInfo p in CategoryProperties(data))
        {
            Type element = p.PropertyType.GetGenericArguments().FirstOrDefault();
            if (element is null || !type.IsAssignableFrom(element))
                continue;
            IList list = (IList)p.GetValue(data);
            foreach (object item in list)
            {
                if (NameOf(item) == name)
                    return item;
            }
        }
        throw new ArgumentException($"No {type.Name} named \"{name}\".");
    }

    #endregion

    #region Describe

    private static bool IsSimple(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);
    }

    private static IEnumerable<(string Name, Type Type, Func<object, object> Get, bool CanWrite)> Members(Type type)
    {
        foreach (PropertyInfo p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true } && p.GetCustomAttribute<ObsoleteAttribute>() is null)
                yield return (p.Name, p.PropertyType, p.GetValue, p.SetMethod is { IsPublic: true });
        }
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            yield return (f.Name, f.FieldType, f.GetValue, !f.IsInitOnly);
    }

    private static JsonNode SimpleValue(object value) => value switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        Enum e => JsonValue.Create(e.ToString()),
        float f => float.IsFinite(f) ? JsonValue.Create(f) : JsonValue.Create(f.ToString(CultureInfo.InvariantCulture)),
        double d => double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
        IConvertible c => JsonValue.Create(System.Convert.ToDecimal(c, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>
    /// Describes an object as JSON: simple values inline, strings as their content, references to
    /// named resources as {"$ref": type, "name": ...}, lists as counts (plus items, up to
    /// <paramref name="maxItems"/>, when <paramref name="depth"/> allows), nested objects recursively.
    /// </summary>
    public static JsonNode Describe(object obj, int depth = 1, int maxItems = 50)
    {
        obj = Unwrap(obj);
        switch (obj)
        {
            case null:
                return null;
            case UndertaleString s:
                return JsonValue.Create(s.Content);
        }
        Type type = obj.GetType();
        if (IsSimple(type))
            return SimpleValue(obj);
        if (obj is IList list)
            return DescribeList(list, depth, maxItems);

        JsonObject result = new() { ["$type"] = FriendlyTypeName(type) };
        foreach (var (name, memberType, get, _) in Members(type))
        {
            object value;
            try
            {
                value = get(obj);
            }
            catch (Exception e)
            {
                result[name] = $"<error: {(e.InnerException ?? e).Message}>";
                continue;
            }
            result[name] = DescribeMember(value, memberType, depth, maxItems);
        }
        return result;
    }

    private static JsonNode DescribeMember(object value, Type memberType, int depth, int maxItems)
    {
        value = Unwrap(value);
        if (value is null)
            return null;
        if (value is UndertaleString s)
            return JsonValue.Create(s.Content);
        if (IsSimple(value.GetType()))
            return SimpleValue(value);
        if (value is UndertaleNamedResource named)
            return new JsonObject { ["$ref"] = FriendlyTypeName(value.GetType()), ["name"] = named.Name?.Content };
        if (value is IList list)
            return depth > 0 ? DescribeList(list, depth - 1, maxItems) : new JsonObject { ["$count"] = list.Count };
        if (value is ICollection collection)
            return new JsonObject { ["$count"] = collection.Count };
        return depth > 0 ? Describe(value, depth - 1, maxItems) : JsonValue.Create(value.ToString());
    }

    private static JsonNode DescribeList(IList list, int depth, int maxItems)
    {
        JsonArray items = new();
        for (int i = 0; i < Math.Min(list.Count, maxItems); i++)
            items.Add(DescribeMember(list[i], typeof(object), depth, maxItems));
        return new JsonObject { ["$count"] = list.Count, ["items"] = items };
    }

    public static string FriendlyTypeName(Type type)
    {
        string name = type.IsNested ? type.DeclaringType!.Name + "." + type.Name : type.Name;
        if (!type.IsGenericType)
            return name;
        return name[..name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName)) + ">";
    }

    #endregion

    #region Paths & editing

    /// <summary>
    /// Splits a path like "Textures[0].Texture.SourceX" or "Layers.2.XOffset" into segments.
    /// </summary>
    private static List<string> SplitPath(string path)
        => path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static object Step(object current, string segment)
    {
        current = Unwrap(current);
        if (current is null)
            throw new ArgumentException($"Can't access \"{segment}\" of null.");
        if (current is IList list && int.TryParse(segment, out int index))
        {
            if (index < 0 || index >= list.Count)
                throw new ArgumentException($"Index {index} out of range (count {list.Count}).");
            return list[index];
        }
        var member = Members(current.GetType()).FirstOrDefault(m => string.Equals(m.Name, segment, StringComparison.OrdinalIgnoreCase));
        if (member.Name is null)
            throw new ArgumentException($"{FriendlyTypeName(current.GetType())} has no member \"{segment}\".");
        return member.Get(current);
    }

    /// <summary>Returns the object at a path relative to <paramref name="root"/> (empty path = root).</summary>
    public static object Navigate(object root, string path)
    {
        object current = root;
        foreach (string segment in SplitPath(path ?? ""))
            current = Step(current, segment);
        return Unwrap(current);
    }

    /// <summary>
    /// Sets the member at <paramref name="path"/>. Values are converted from JSON: numbers, booleans,
    /// enum names, strings (UndertaleString members get a new/existing string from the string table),
    /// and resource names for references to named resources (null clears them).
    /// </summary>
    /// <returns>The member's value after setting it, described as JSON.</returns>
    public static JsonNode SetValue(UndertaleData data, object root, string path, JsonNode value)
    {
        List<string> segments = SplitPath(path);
        if (segments.Count == 0)
            throw new ArgumentException("Empty property path.");
        object parent = root;
        foreach (string segment in segments.Take(segments.Count - 1))
            parent = Step(parent, segment);
        parent = Unwrap(parent);
        string last = segments[^1];

        if (parent is IList list && int.TryParse(last, out int index))
        {
            Type elementType = list.GetType().GetGenericArguments().FirstOrDefault() ?? typeof(object);
            list[index] = Convert(data, value, elementType);
            return Describe(list[index], 0);
        }

        Type type = parent.GetType();
        PropertyInfo property = type.GetProperty(last, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        FieldInfo field = property is null ? type.GetField(last, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) : null;
        if (property is null && field is null)
            throw new ArgumentException($"{FriendlyTypeName(type)} has no member \"{last}\".");
        if (property is not null && property.SetMethod is not { IsPublic: true })
            throw new ArgumentException($"{FriendlyTypeName(type)}.{property.Name} is read-only.");
        if (field is not null && field.IsInitOnly)
            throw new ArgumentException($"{FriendlyTypeName(type)}.{field.Name} is read-only.");
        if (type.IsValueType)
            throw new ArgumentException($"Can't set members of value type {FriendlyTypeName(type)} in place.");

        Type memberType = property?.PropertyType ?? field!.FieldType;
        object converted = Convert(data, value, memberType);
        if (property is not null)
            property.SetValue(parent, converted);
        else
            field!.SetValue(parent, converted);
        return Describe(property is not null ? property.GetValue(parent) : field!.GetValue(parent), 0);
    }

    /// <summary>Converts a JSON value to a member type.</summary>
    public static object Convert(UndertaleData data, JsonNode value, Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type);
        if (value is null)
        {
            if (!type.IsValueType || underlying is not null)
                return null;
            throw new ArgumentException($"null is not a valid {type.Name}.");
        }
        type = underlying ?? type;

        if (type == typeof(UndertaleString))
            return data.Strings.MakeString(value.GetValue<object>()?.ToString());
        if (type == typeof(string))
            return value is JsonValue v && v.TryGetValue(out string s) ? s : value.ToJsonString();
        if (type == typeof(bool))
        {
            if (value is JsonValue bv && bv.TryGetValue(out bool b))
                return b;
            return bool.Parse(value.ToString());
        }
        if (type.IsEnum)
        {
            if (value is JsonValue ev && ev.TryGetValue(out string name))
                return Enum.Parse(type, name, ignoreCase: true);
            return Enum.ToObject(type, value.GetValue<long>());
        }
        if (type.IsPrimitive || type == typeof(decimal))
        {
            string text = value is JsonValue nv && nv.TryGetValue(out string str) ? str : value.ToJsonString();
            return System.Convert.ChangeType(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), type, CultureInfo.InvariantCulture);
        }
        if (typeof(UndertaleNamedResource).IsAssignableFrom(type) || type.IsInterface)
        {
            string resourceName = value.GetValue<string>();
            return FindResourceOfType(data, type, resourceName);
        }
        throw new ArgumentException($"Can't assign values of type {FriendlyTypeName(type)} from JSON.");
    }

    #endregion
}
