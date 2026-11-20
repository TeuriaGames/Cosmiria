namespace Cosmiria;

/// <summary>
/// Generates this component's <see cref="IComponent.Render"/> from a CML markup file.
/// The path is relative to the C# file containing the attribute, and the file must be
/// part of the build as an additional file: <c>&lt;AdditionalFiles Include="**/*.cml" /&gt;</c>.
/// <code>
/// [Cml("Counter.cml")]
/// public partial struct Counter { public int Value; public void Add(int n) => Value += n; }  // onclick="Add(1)"
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class CmlAttribute : Attribute
{
    public string Path { get; }

    public CmlAttribute(string path) => Path = path;
}
