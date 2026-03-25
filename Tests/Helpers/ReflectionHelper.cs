using System.Reflection;

namespace TcpClientEvolution.Tests.Helpers;

/// <summary>
/// Helper per accedere a campi e proprietà private tramite reflection.
/// Usato nei test per verificare lo stato interno dei client.
/// </summary>
public static class ReflectionHelper
{
    public static T? GetField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        return field != null ? (T?)field.GetValue(obj) : default;
    }

    public static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        field?.SetValue(obj, value);
    }

    public static T? GetStaticField<T>(Type type, string fieldName)
    {
        var field = type.GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        return field != null ? (T?)field.GetValue(null) : default;
    }

    public static void SetStaticField(Type type, string fieldName, object? value)
    {
        var field = type.GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        field?.SetValue(null, value);
    }

    public static bool HasProperty(Type type, string propertyName)
    {
        return type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) != null;
    }

    public static bool HasEvent(Type type, string eventName)
    {
        return type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance) != null;
    }

    public static bool HasField(Type type, string fieldName)
    {
        return type.GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static) != null;
    }
}
