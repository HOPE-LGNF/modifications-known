using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ModificationsKnown.Core.Mods;

namespace ModificationsKnown.Infrastructure.BepInEx;

public sealed record BepInPluginMetadata(
    string? Guid,
    string? Name,
    string? Version,
    IReadOnlyList<ModDependency> Dependencies);

/// <summary>
/// Reads BepInEx attributes directly from assembly metadata. The DLL is never
/// loaded into the launcher process, so a broken or hostile plugin cannot run
/// during a scan.
/// </summary>
public static class BepInExAssemblyReader
{
    public static BepInPluginMetadata Read(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);
            var reader = peReader.GetMetadataReader();

            string? guid = null;
            string? name = null;
            string? version = null;
            var dependencies = new List<ModDependency>();

            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                foreach (var attributeHandle in type.GetCustomAttributes())
                {
                    var attribute = reader.GetCustomAttribute(attributeHandle);
                    var typeName = GetAttributeTypeName(reader, attribute.Constructor);
                    if (typeName is null) continue;

                    if (IsAttribute(typeName, "BepInPlugin") && guid is null)
                    {
                        var values = ReadBepInPluginValues(reader, attribute);
                        guid = values.Guid;
                        name = values.Name;
                        version = values.Version;
                    }
                    else if (IsAttribute(typeName, "BepInDependency"))
                    {
                        var dependency = ReadBepInDependency(reader, attribute);
                        if (dependency is not null) dependencies.Add(dependency);
                    }
                }
            }

            return new BepInPluginMetadata(guid, name, version, dependencies);
        }
        catch
        {
            return new BepInPluginMetadata(null, null, null, []);
        }
    }

    private static bool IsAttribute(string typeName, string shortName) =>
        string.Equals(typeName, shortName, StringComparison.Ordinal)
        || string.Equals(typeName, shortName + "Attribute", StringComparison.Ordinal)
        || typeName.EndsWith("." + shortName, StringComparison.Ordinal)
        || typeName.EndsWith("." + shortName + "Attribute", StringComparison.Ordinal);

    private static string? GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        try
        {
            if (constructor.Kind == HandleKind.MemberReference)
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)constructor);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                    var ns = reader.GetString(type.Namespace);
                    var name = reader.GetString(type.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }

                if (member.Parent.Kind == HandleKind.TypeDefinition)
                {
                    var type = reader.GetTypeDefinition((TypeDefinitionHandle)member.Parent);
                    return reader.GetString(type.Name);
                }
            }
            else if (constructor.Kind == HandleKind.MethodDefinition)
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
                var type = reader.GetTypeDefinition(method.GetDeclaringType());
                return reader.GetString(type.Name);
            }
        }
        catch
        {
            // Ignore malformed metadata and continue scanning.
        }

        return null;
    }

    private static (string? Guid, string? Name, string? Version) ReadBepInPluginValues(
        MetadataReader reader,
        CustomAttribute attribute)
    {
        try
        {
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) return (null, null, null);

            return (
                ReadSerializedString(ref blob),
                ReadSerializedString(ref blob),
                ReadSerializedString(ref blob));
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static ModDependency? ReadBepInDependency(MetadataReader reader, CustomAttribute attribute)
    {
        try
        {
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) return null;

            var guid = ReadSerializedString(ref blob);
            if (string.IsNullOrWhiteSpace(guid)) return null;

            string? minimumVersion = null;
            var isHard = true;

            // Constructors are either (guid), (guid, flags), (guid, version) or
            // (guid, version, flags). A four-byte tail is the flags-only shape.
            if (blob.RemainingBytes == 4)
            {
                var flags = blob.ReadInt32();
                isHard = (flags & 1) == 0;
            }
            else
            {
                minimumVersion = ReadSerializedString(ref blob);
                if (blob.RemainingBytes >= 4)
                {
                    var flags = blob.ReadInt32();
                    isHard = (flags & 1) == 0;
                }
            }

            return new ModDependency(guid, string.IsNullOrWhiteSpace(minimumVersion) ? null : minimumVersion, isHard);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadSerializedString(ref BlobReader reader)
    {
        if (reader.RemainingBytes == 0) return null;

        var first = reader.ReadByte();
        if (first == 0xFF) return null;

        var length = first switch
        {
            < 0x80 => first,
            < 0xC0 => ((first & 0x3F) << 8) | reader.ReadByte(),
            _ => ((first & 0x1F) << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte()
        };

        if (length <= 0) return string.Empty;
        if (length > reader.RemainingBytes) return null;

        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}
