namespace Krosforge.Runtime.Native.Protocol.Connection;

/// <summary>Owned decoded request; its payload remains valid after the pipe advances.</summary>
public sealed record ConnectionRequest(string CorrelationId, int Operation, ReadOnlyMemory<byte> Payload);
