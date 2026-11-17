namespace Krosforge.Runtime.Native.Protocol;

/// <summary>Rejects malformed or unsupported wire input without treating it as a game success.</summary>
public sealed class ProtocolException(string message) : Exception(message);
