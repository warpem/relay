using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Warp.Tools;
using Serilog;

namespace Refund.DataModel;

/// <summary>
/// Base class for all serializable entities in the Relay system. Provides core functionality for
/// JSON serialization/deserialization, state adoption, and property management.
/// 
/// This class serves as the foundation for the entire data model hierarchy and implements a 
/// custom attribute-based serialization system that supports a wide range of data types, 
/// including primitive types, arrays, vectors, and nested RelayBase objects.
/// </summary>
[Serializable]
public class RelayBase
{
    /// <summary>
    /// Cache of properties to serialize for each type, indexed by type.
    /// This improves performance by avoiding reflection lookups for each serialization operation.
    /// </summary>
    private static Dictionary<Type, (string Name, PropertyInfo Prop)[]> TypeProperties = new Dictionary<Type, (string Name, PropertyInfo Prop)[]>();

    /// <summary>
    /// Serializes this object's properties to the specified JSON node.
    /// Handles serialization of various data types including primitives, arrays, vectors, and nested RelayBase objects.
    /// Only properties decorated with the [RelayProperty] attribute will be serialized.
    /// </summary>
    /// <param name="writer">The JSON node to write property values to</param>
    public virtual void WriteToJson(JsonNode writer)
    {
        var namedProps = GetNamedProperties(GetType());

        foreach ((string name, PropertyInfo prop) in namedProps)
        {
            try
            {
                if (prop.PropertyType == typeof(string))
                    writer[name] = JsonValue.Create((string)prop.GetValue(this));

                else if (prop.PropertyType == typeof(string[]))
                    writer[name] = new JsonArray(((string[])prop.GetValue(this)).Select(v => JsonValue.Create(v)).ToArray<JsonNode>());

                else if (prop.PropertyType == typeof(bool))
                    writer[name] = (bool)prop.GetValue(this);

                else if (prop.PropertyType == typeof(int))
                    writer[name] = (int)prop.GetValue(this);

                else if (prop.PropertyType == typeof(int?))
                    writer[name] = (int?)prop.GetValue(this);

                else if (prop.PropertyType == typeof(int[]))
                    writer[name] = new JsonArray(((int[])prop.GetValue(this)).Select(v => JsonValue.Create(v)).ToArray<JsonNode>());

                else if (prop.PropertyType == typeof(long))
                    writer[name] = (long)prop.GetValue(this);

                else if (prop.PropertyType == typeof(long[]))
                    writer[name] = new JsonArray(((long[])prop.GetValue(this)).Select(v => JsonValue.Create(v)).ToArray<JsonNode>());

                else if (prop.PropertyType == typeof(float))
                    writer[name] = (float)prop.GetValue(this);

                else if (prop.PropertyType == typeof(float[]))
                    writer[name] = new JsonArray(((float[])prop.GetValue(this)).Select(v => JsonValue.Create(v)).ToArray<JsonNode>());

                else if (prop.PropertyType == typeof(double))
                    writer[name] = (double)prop.GetValue(this);

                else if (prop.PropertyType == typeof(double[]))
                    writer[name] = new JsonArray(((double[])prop.GetValue(this)).Select(v => JsonValue.Create(v)).ToArray<JsonNode>());

                else if (prop.PropertyType == typeof(decimal))
                    writer[name] = (double)(decimal)prop.GetValue(this);

                else if (prop.PropertyType == typeof(decimal?))
                    writer[name] = (double?)(decimal?)prop.GetValue(this);

                else if (prop.PropertyType == typeof(decimal[]))
                    writer[name] = new JsonArray(((decimal[])prop.GetValue(this)).Select(v => JsonValue.Create((double)v)).ToArray<JsonNode>());

                else if (prop.PropertyType == typeof(int2))
                {
                    int2 val = (int2)prop.GetValue(this);
                    writer[name] = new JsonArray(JsonValue.Create(val.X), JsonValue.Create(val.Y));
                }

                else if (prop.PropertyType == typeof(int3))
                {
                    int3 val = (int3)prop.GetValue(this);
                    writer[name] = new JsonArray(JsonValue.Create(val.X), JsonValue.Create(val.Y), JsonValue.Create(val.Z));
                }

                else if (prop.PropertyType == typeof(int4))
                {
                    int4 val = (int4)prop.GetValue(this);
                    writer[name] = new JsonArray(JsonValue.Create(val.X), JsonValue.Create(val.Y), JsonValue.Create(val.Z), JsonValue.Create(val.W));
                }

                else if (prop.PropertyType == typeof(float2))
                {
                    float2 val = (float2)prop.GetValue(this);
                    writer[name] = new JsonArray(JsonValue.Create(val.X), JsonValue.Create(val.Y));
                }

                else if (prop.PropertyType == typeof(float3))
                {
                    float3 val = (float3)prop.GetValue(this);
                    writer[name] = new JsonArray(JsonValue.Create(val.X), JsonValue.Create(val.Y), JsonValue.Create(val.Z));
                }

                else if (prop.PropertyType == typeof(float4))
                {
                    float4 val = (float4)prop.GetValue(this);
                    writer[name] = new JsonArray(JsonValue.Create(val.X), JsonValue.Create(val.Y), JsonValue.Create(val.Z), JsonValue.Create(val.W));
                }

                else if (prop.PropertyType == typeof(Guid))
                    writer[name] = ((Guid)prop.GetValue(this)).ToString();

                else if (prop.PropertyType == typeof(DateTime))
                    writer[name] = ((DateTime)prop.GetValue(this)).ToString("s", CultureInfo.InvariantCulture);

                else if (prop.PropertyType == typeof(DateTime?))
                    writer[name] = ((DateTime?)prop.GetValue(this))?.ToString("s", CultureInfo.InvariantCulture) ?? null;

                else if (prop.PropertyType.IsEnum)
                    writer[name] = prop.GetValue(this).ToString();

                else if (prop.PropertyType.IsSubclassOf(typeof(RelayBase)))
                {
                    JsonNode propNode = new JsonObject();
                    ((RelayBase)prop.GetValue(this)).WriteToJson(propNode);
                    writer[name] = propNode;
                }

                else if (prop.PropertyType.IsGenericType &&
                         prop.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                         prop.PropertyType.GetGenericArguments()[0] == typeof(string))
                {
                    var dict = (IDictionary)prop.GetValue(this);
                    var valueType = prop.PropertyType.GetGenericArguments()[1];
                    var obj = new JsonObject();

                    if (dict != null)
                    {
                        foreach (DictionaryEntry entry in dict)
                        {
                            if (valueType.IsSubclassOf(typeof(RelayBase)))
                            {
                                JsonNode entryNode = new JsonObject();
                                ((RelayBase)entry.Value).WriteToJson(entryNode);
                                obj[(string)entry.Key] = entryNode;
                            }
                            else
                            {
                                obj[(string)entry.Key] = JsonSerializer.SerializeToNode(entry.Value);
                            }
                        }
                    }

                    writer[name] = obj;
                }

                else
                    writer[name] = JsonSerializer.SerializeToNode(prop.GetValue(this));
            }
            catch (Exception ex)
            {
                string message = $"Couldn't serialize {name} of type {prop.PropertyType}, value = {prop.GetValue(this)}";
                Log.ForContext<RelayBase>().Error(ex, message);
                throw new Exception(message);
            }
        }
    }

    /// <summary>
    /// Converts this object to a JSON node, including all serializable properties.
    /// </summary>
    /// <returns>A JsonNode representing this object's state</returns>
    public virtual JsonNode ToJson()
    {
        JsonNode writer = new JsonObject();
        WriteToJson(writer);
        return writer;
    }

    /// <summary>
    /// Converts this object to a JSON string, including all serializable properties.
    /// The resulting string is not indented to minimize size.
    /// </summary>
    /// <returns>A JSON string representation of this object's state</returns>
    public virtual string ToJsonString()
    {
        return ToJson().ToJsonString(new JsonSerializerOptions() { WriteIndented = false });
    }

    /// <summary>
    /// Deserializes this object's properties from the specified JSON node.
    /// Handles deserialization of various data types including primitives, arrays, vectors, and nested RelayBase objects.
    /// Only properties decorated with the [RelayProperty] attribute will be deserialized.
    /// </summary>
    /// <param name="reader">The JSON node to read property values from</param>
    public virtual void ReadFromJson(JsonNode reader)
    {
        var namedProps = GetNamedProperties(GetType());

        foreach ((string name, PropertyInfo prop) in namedProps)
        {
            if (reader[name] == null)
                continue;
            
            try
            {
                // Case 1: RelayBase-derived types need recursive processing
                if (prop.PropertyType.IsSubclassOf(typeof(RelayBase)))
                {
                    RelayBase instance = (RelayBase)Activator.CreateInstance(prop.PropertyType);
                    instance.ReadFromJson(reader[name]);
                    prop.SetValue(this, instance);
                }
                // Case 2: Decimal types need conversion from double
                else if (prop.PropertyType == typeof(decimal))
                    prop.SetValue(this, (decimal)reader[name].Deserialize<double>());
                else if (prop.PropertyType == typeof(decimal?))
                    prop.SetValue(this, (decimal?)reader[name].Deserialize<double?>());
                else if (prop.PropertyType == typeof(decimal[]))
                    prop.SetValue(this, reader[name].Deserialize<double[]>().Select(v => (decimal)v).ToArray());
                // Case 3: DateTime types need special parsing
                else if (prop.PropertyType == typeof(DateTime))
                    prop.SetValue(this, DateTime.ParseExact(reader[name].Deserialize<string>(), "s", CultureInfo.InvariantCulture));
                else if (prop.PropertyType == typeof(DateTime?))
                {
                    var value = reader[name].Deserialize<string>();
                    if (value == null)
                        prop.SetValue(this, (DateTime?)null);
                    else
                        prop.SetValue(this, DateTime.ParseExact(value, "s", CultureInfo.InvariantCulture));
                }
                // Case 4: Vector types with custom array format
                else if (prop.PropertyType == typeof(int2))
                {
                    int[] vals = reader[name].Deserialize<int[]>();
                    prop.SetValue(this, new int2(vals[0], vals[1]));
                }
                else if (prop.PropertyType == typeof(int3))
                {
                    int[] vals = reader[name].Deserialize<int[]>();
                    prop.SetValue(this, new int3(vals[0], vals[1], vals[2]));
                }
                else if (prop.PropertyType == typeof(int4))
                {
                    int[] vals = reader[name].Deserialize<int[]>();
                    prop.SetValue(this, new int4(vals[0], vals[1], vals[2], vals[3]));
                }
                else if (prop.PropertyType == typeof(float2))
                {
                    float[] vals = reader[name].Deserialize<float[]>();
                    prop.SetValue(this, new float2(vals[0], vals[1]));
                }
                else if (prop.PropertyType == typeof(float3))
                {
                    float[] vals = reader[name].Deserialize<float[]>();
                    prop.SetValue(this, new float3(vals[0], vals[1], vals[2]));
                }
                else if (prop.PropertyType == typeof(float4))
                {
                    float[] vals = reader[name].Deserialize<float[]>();
                    prop.SetValue(this, new float4(vals[0], vals[1], vals[2], vals[3]));
                }
                // Case 5: Enum types
                else if (prop.PropertyType.IsEnum)
                    prop.SetValue(this, Enum.Parse(prop.PropertyType, reader[name].Deserialize<string>()));
                // Case 6: Dictionary<string, T> types
                else if (prop.PropertyType.IsGenericType &&
                         prop.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
                         prop.PropertyType.GetGenericArguments()[0] == typeof(string))
                {
                    var valueType = prop.PropertyType.GetGenericArguments()[1];
                    var dict = (IDictionary)Activator.CreateInstance(prop.PropertyType);
                    var jsonObj = reader[name] as JsonObject;

                    if (jsonObj != null)
                    {
                        foreach (var kvp in jsonObj)
                        {
                            if (valueType.IsSubclassOf(typeof(RelayBase)))
                            {
                                var instance = (RelayBase)Activator.CreateInstance(valueType);
                                instance.ReadFromJson(kvp.Value);
                                dict[kvp.Key] = instance;
                            }
                            else
                            {
                                var jsonString = kvp.Value.ToJsonString();
                                dict[kvp.Key] = JsonSerializer.Deserialize(jsonString, valueType);
                            }
                        }
                    }

                    prop.SetValue(this, dict);
                }
                // Case 7: Everything else - use System.Text.Json
                else
                {
                    var jsonString = reader[name].ToJsonString();
                    var result = JsonSerializer.Deserialize(jsonString, prop.PropertyType);
                    prop.SetValue(this, result);
                }
            }
            catch (Exception ex)
            {
                string message = $"Couldn't deserialize {name} of type {prop.PropertyType}, value = {reader[name]}";
                Log.ForContext<RelayBase>().Error(ex, message);
                //throw new Exception(message);
            }
        }
    }

    /// <summary>
    /// Copies the state from another RelayBase object of the same type.
    /// This performs a deep copy of all serializable properties, adopting all values from the source object.
    /// For nested RelayBase objects, the AdoptState method is called recursively.
    /// </summary>
    /// <param name="adoptFrom">The source object to adopt state from</param>
    /// <returns>True if any property values were changed during adoption, false otherwise</returns>
    /// <exception cref="Exception">Thrown if the source object is not of the same type as this object</exception>
    public virtual bool AdoptState(RelayBase adoptFrom)
    {
        if (GetType() != adoptFrom.GetType())
            throw new Exception("Both objects must be of the same type");

        bool stateHasChanged = false;

        var namedProps = GetNamedProperties(GetType());

        foreach ((string name, PropertyInfo prop) in namedProps.Where(np => !np.Prop.IsDefined(typeof(SkipAdoption))))
        {
            if (prop.PropertyType.IsSubclassOf(typeof(RelayBase)))
                stateHasChanged |= ((RelayBase)prop.GetValue(this)).AdoptState((RelayBase)prop.GetValue(adoptFrom));
            else
                stateHasChanged |= AdoptProperty(prop, adoptFrom);
        }

        return stateHasChanged;
    }

    /// <summary>
    /// Adopts a single property value from another RelayBase object.
    /// </summary>
    /// <typeparam name="T">The type of the property</typeparam>
    /// <param name="prop">The PropertyInfo of the property to adopt</param>
    /// <param name="adoptFrom">The source object to adopt the property value from</param>
    /// <returns>True if the property value was changed, false otherwise</returns>
    private bool AdoptProperty(PropertyInfo prop, RelayBase adoptFrom)
    {
        object old = prop.GetValue(this);
        object @new = prop.GetValue(adoptFrom);

        if (old == null && @new == null)
            return false;

        if (prop.PropertyType.IsArray && @new != null)
        {
            Array newArray = (Array)@new;

            Array copiedArray = (Array)newArray.Clone();
            prop.SetValue(this, copiedArray);
                
            return false;
        }
        else if (typeof(IList).IsAssignableFrom(prop.PropertyType) && @new != null)
        {
            IList newList = (IList)@new;

            IList copiedList = (IList)Activator.CreateInstance(newList.GetType());

            foreach (var item in newList)
                copiedList.Add(item);
            
            prop.SetValue(this, copiedList);
                
            return false;
        }
        else if (old == null || @new == null || !old.Equals(@new))
        {
            prop.SetValue(this, @new);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Adopts an array property value from another RelayBase object.
    /// Arrays are compared element by element to determine if a change is needed.
    /// </summary>
    /// <typeparam name="T">The element type of the array property</typeparam>
    /// <param name="prop">The PropertyInfo of the array property to adopt</param>
    /// <param name="adoptFrom">The source object to adopt the array property value from</param>
    /// <returns>True if the array property value was changed, false otherwise</returns>
    private bool AdoptArrayProperty<T>(PropertyInfo prop, RelayBase adoptFrom)
    {
        T[] old = (T[])prop.GetValue(this)!;
        T[] @new = (T[])prop.GetValue(adoptFrom)!;

        if (!Helper.AreElementsEqual(old, @new))
        {
            prop.SetValue(this, @new);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Populates the TypeProperties cache with the serializable properties of the specified type.
    /// Only properties decorated with the [RelayProperty] attribute are included.
    /// Properties are ordered according to the Order property of the RelayProperty attribute.
    /// </summary>
    /// <param name="type">The type to populate properties for</param>
    private static void PopulateProperties(Type type)
    {
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        List<PropertyInfo> serializableProps = properties.Where(p => p.IsDefined(typeof(RelayProperty)))
                                                         .OrderBy(p => ((RelayProperty)p.GetCustomAttribute(typeof(RelayProperty))).Order)
                                                         .ToList();
        List<string> propNames = serializableProps.Select(p =>
        {
            RelayProperty a = p.GetCustomAttribute(typeof(RelayProperty)) as RelayProperty;
            if (string.IsNullOrEmpty(a.Alias))
                return p.Name;
            else
                return a.Alias;
        }).ToList();

        var namedProps = new (string Name, PropertyInfo Prop)[serializableProps.Count];
        for (int i = 0; i < namedProps.Length; i++)
            namedProps[i] = (propNames[i], serializableProps[i]);

        TypeProperties.Add(type, namedProps);
    }

    /// <summary>
    /// Gets the named properties for a specific type, using cached values when available.
    /// If the properties for the type have not been cached yet, they are populated first.
    /// This method is thread-safe.
    /// </summary>
    /// <param name="type">The type to get properties for</param>
    /// <returns>An array of named property tuples containing the property name and PropertyInfo</returns>
    private static (string Name, PropertyInfo Prop)[] GetNamedProperties(Type type)
    {
        (string Name, PropertyInfo Prop)[] namedProps;

        lock (TypeProperties)
        {
            if (!TypeProperties.ContainsKey(type))
                PopulateProperties(type);

            namedProps = TypeProperties[type];
        }

        return namedProps;
    }
}

/// <summary>
/// Delegate for property change notifications.
/// Used when a property value has changed and listeners need to be notified.
/// </summary>
/// <param name="sender">The object that initiated the change</param>
/// <param name="oldValue">The previous value of the property</param>
/// <param name="newValue">The new value of the property</param>
public delegate void NotifiedPropertyChanged(object sender, object oldValue, object newValue);

/// <summary>
/// Attribute used to mark properties for serialization in RelayBase objects.
/// Only properties decorated with this attribute will be included in serialization/deserialization.
/// </summary>
public class RelayProperty : Attribute
{
    /// <summary>
    /// Optional alias to use for the property name during serialization.
    /// If not specified, the property name is used.
    /// </summary>
    public string? Alias = null;
    
    /// <summary>
    /// Determines the order in which properties are serialized.
    /// Properties with lower order values are serialized first.
    /// </summary>
    public int Order = 0;

    /// <summary>
    /// Creates a new RelayProperty attribute.
    /// </summary>
    /// <param name="alias">Optional alias to use for the property name during serialization</param>
    public RelayProperty(string? alias = null)
    {
        Alias = alias;
    }
}

/// <summary>
/// Attribute used to mark properties that should be cleared when a model is reset.
/// This is typically used for properties that hold temporary or calculated data.
/// </summary>
public class Clearable : Attribute { }

public class SkipAdoption : Attribute { }