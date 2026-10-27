using System.Diagnostics.CodeAnalysis;

namespace CamBridge.Core;

/// <summary>GenICam node interface types exposed through <see cref="ICamera"/>.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names mirror GenICam interface types (IInteger, IFloat, IString).")]
public enum ParameterType
{
    Integer,
    Float,
    Boolean,
    Enumeration,
    String,
    Command,
}
