using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Roam;

// Reads a managed assembly's declared versions WITHOUT loading it into the runtime. We parse the
// PE/CLI metadata directly with System.Reflection.Metadata (MetadataReader over the assembly's
// CustomAttributes) rather than Assembly.LoadFrom, because:
//   - the assembly being inspected is a foreign, possibly cross-RID (win-x64) self-contained
//     publish output that the running roam process must never try to JIT or resolve dependencies for;
//   - LoadFrom takes a lock on the file and pins it for the process lifetime;
//   - metadata reading is fully cross-platform — a Linux controller can read a win-x64 assembly's
//     informational version, which is exactly the deploy-provenance case.
//
// The whole read is wrapped so a native (non-managed) DLL, a corrupt file, or a netmodule simply
// yields null rather than throwing: the caller treats null as "not a managed assembly, skip it".
public static class AssemblyVersionReader
{
    // Returns the assembly's declared versions, or null when the file is not a managed assembly
    // (native DLL, resource-only, unreadable, or a module without an assembly row).
    public static AssemblyVersionInfo? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            var assembly = reader.GetAssemblyDefinition();
            var assemblyVersion = assembly.Version.ToString();

            string? informational = null;
            string? fileVersion = null;
            foreach (var handle in assembly.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                switch (GetAttributeTypeName(reader, attribute))
                {
                    case "System.Reflection.AssemblyInformationalVersionAttribute":
                        informational = ReadSingleStringArgument(reader, attribute);
                        break;
                    case "System.Reflection.AssemblyFileVersionAttribute":
                        fileVersion = ReadSingleStringArgument(reader, attribute);
                        break;
                }
            }

            return new AssemblyVersionInfo(informational, fileVersion, assemblyVersion);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Native DLLs, truncated files, and anything else that doesn't parse as managed
            // metadata are simply "not an assembly we can report on" — never fatal to a deploy.
            return null;
        }
    }

    // The fully-qualified type name of a custom attribute, resolved from its constructor handle.
    // Framework attributes are MemberReferences whose Parent is a TypeReference; an attribute defined
    // in the assembly itself is a MethodDefinition on a TypeDefinition. Both are handled.
    private static string? GetAttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (member.Parent.Kind == HandleKind.TypeReference)
                {
                    var typeReference = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                    return Combine(reader.GetString(typeReference.Namespace), reader.GetString(typeReference.Name));
                }

                return null;

            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var declaringType = reader.GetTypeDefinition(method.GetDeclaringType());
                return Combine(reader.GetString(declaringType.Namespace), reader.GetString(declaringType.Name));

            default:
                return null;
        }
    }

    private static string Combine(string @namespace, string name)
        => string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";

    // The three version attributes each have a single string constructor argument. Rather than stand
    // up a full ICustomAttributeTypeProvider for DecodeValue, parse the CA blob directly: a 2-byte
    // prolog (0x0001) followed by a SerString. BlobReader.ReadSerializedString handles the compressed
    // length prefix and the 0xFF null marker.
    private static string? ReadSingleStringArgument(MetadataReader reader, CustomAttribute attribute)
    {
        var blob = reader.GetBlobReader(attribute.Value);
        if (blob.Length < 2)
        {
            return null;
        }

        if (blob.ReadUInt16() != 0x0001)
        {
            return null;
        }

        return blob.ReadSerializedString();
    }
}

// One managed assembly's declared versions. All nullable: a stripped or older assembly may omit the
// informational/file attributes, in which case the caller falls back informational -> file -> assembly.
public sealed record AssemblyVersionInfo(
    string? InformationalVersion,
    string? FileVersion,
    string? AssemblyVersion)
{
    // The single version string to surface to a human/agent: prefer the informational version
    // (carries the NuGet/GitVersion semver, e.g. 1.5.1-alpha.1+<sha>), then the file version, then
    // the assembly version. "(unknown)" only when an assembly declares none of the three.
    public string Display
        => FirstNonEmpty(InformationalVersion) ?? FirstNonEmpty(FileVersion) ?? FirstNonEmpty(AssemblyVersion) ?? "(unknown)";

    private static string? FirstNonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
