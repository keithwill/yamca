using System.Text.Json.Serialization;
using Yamca.Agent.Board;

namespace Yamca.Agent.Storage;

/// <summary>The single source-generated <see cref="JsonSerializerContext"/> that VestPocket uses to
/// (de)serialize every type stored in <c>yamca.db</c>. VestPocket takes one context per store and
/// stamps its own <c>$type</c> discriminator per value, so unrelated record types share the one store
/// with no polymorphism plumbing. As features add stored types, add a <c>[JsonSerializable]</c> line
/// here and a matching <c>AddType&lt;…&gt;()</c> call in <see cref="YamcaStore"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ColumnRecord))]
[JsonSerializable(typeof(CardRecord))]
[JsonSerializable(typeof(CardCounter))]
internal partial class YamcaStoreJsonContext : JsonSerializerContext;
