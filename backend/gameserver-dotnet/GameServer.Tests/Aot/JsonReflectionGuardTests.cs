using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using GameServer.World;

namespace GameServer.Tests.Aot;

/// <summary>
/// Guards the precondition that makes the <c>Collections.Pooled</c> AOT warnings
/// unreachable.
///
/// <para><b>The problem being guarded.</b> Arch pulls in
/// <c>Collections.Pooled 2.0.0-preview.27</c>, whose <c>PooledList&lt;T&gt;</c>,
/// <c>PooledSet&lt;T&gt;</c>, <c>PooledQueue&lt;T&gt;</c>, <c>PooledStack&lt;T&gt;</c>,
/// <c>PooledCollection&lt;T&gt;</c> and <c>PooledObservableCollection&lt;T&gt;</c> each
/// carry <c>[JsonConverter(typeof(PooledEnumerableJsonConverter))]</c>. That attribute
/// roots the converter, so ILC compiles it into the binary and reports 37 IL2026/IL3050
/// diagnostics against it — it calls <c>Type.MakeGenericType</c>,
/// <c>JsonSerializerOptions.GetConverter</c> and the reflection-based
/// <c>JsonSerializer.Serialize/Deserialize</c> overloads.</para>
///
/// <para><b>Why it cannot fire today.</b> A <c>JsonConverterFactory</c> is only invoked
/// when System.Text.Json builds a <c>JsonTypeInfo</c> for an annotated type. This server
/// never asks it to: every <c>JsonSerializer</c> call passes a source-generated
/// <c>JsonTypeInfo</c>, so the reflection-based resolver — the only thing that consults
/// <c>[JsonConverter]</c> on an arbitrary type — is never reached.</para>
///
/// <para><b>Why that needs a guard rather than a comment.</b> The justification is a
/// property of *our* code, not of the dependency. One reflection-based
/// <c>JsonSerializer.Serialize(value)</c> added later would re-enable the resolver, and
/// nothing about that edit would look dangerous in review. This is the same failure
/// shape as the missing Arch AOT hint (ADR-11): invisible at build time, fatal on the
/// path that first exercises it.</para>
///
/// <para>This test reads the compiled GameServer assembly's metadata rather than its
/// source, so it sees generated code and cannot be fooled by a <c>using</c> alias or a
/// helper wrapper.</para>
/// </summary>
public class JsonReflectionGuardTests
{
    /// <summary>
    /// Types that make a <c>JsonSerializer</c> call AOT-safe by supplying the contract
    /// statically instead of deriving it by reflection.
    /// </summary>
    private static readonly string[] SafeContractParameters =
    {
        "JsonTypeInfo",          // System.Text.Json.Serialization.Metadata.JsonTypeInfo[`1]
        "JsonSerializerContext", // System.Text.Json.Serialization.JsonSerializerContext
    };

    [Fact]
    public void GameServer_UsesOnlySourceGeneratedJsonSerialization()
    {
        string assemblyPath = typeof(EcsWorld).Assembly.Location;
        Assert.True(File.Exists(assemblyPath), $"assembly not found: {assemblyPath}");

        var offenders = new List<string>();

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        var provider = new NameOnlySignatureProvider();

        foreach (MemberReferenceHandle handle in md.MemberReferences)
        {
            MemberReference member = md.GetMemberReference(handle);
            if (member.GetKind() != MemberReferenceKind.Method) continue;
            if (member.Parent.Kind != HandleKind.TypeReference) continue;

            var declaringType = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (md.GetString(declaringType.Name) != "JsonSerializer") continue;

            string methodName = md.GetString(member.Name);
            MethodSignature<string> signature = member.DecodeMethodSignature(provider, genericContext: null);

            bool hasStaticContract = signature.ParameterTypes.Any(
                p => SafeContractParameters.Any(safe => p.Contains(safe, StringComparison.Ordinal)));

            if (!hasStaticContract)
            {
                offenders.Add($"JsonSerializer.{methodName}({string.Join(", ", signature.ParameterTypes)})");
            }
        }

        Assert.True(offenders.Count == 0,
            "GameServer references reflection-based System.Text.Json overloads. This re-enables the " +
            "reflection resolver, which is the only thing that would ever invoke " +
            "Collections.Pooled's PooledEnumerableJsonConverter — the type behind the 37 IL2026/IL3050 " +
            "warnings the NativeAOT publish reports. Those warnings are justified in GameServer.csproj " +
            "on the assumption this test enforces.\n" +
            "Use an overload taking a source-generated JsonTypeInfo or JsonSerializerContext:\n  " +
            string.Join("\n  ", offenders.Distinct()));
    }

    /// <summary>
    /// Emits the bare type name for each element of a signature. Enough to spot a
    /// <c>JsonTypeInfo</c> / <c>JsonSerializerContext</c> parameter, and far less code
    /// than a full type-name reconstruction.
    /// </summary>
    private sealed class NameOnlySignatureProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
