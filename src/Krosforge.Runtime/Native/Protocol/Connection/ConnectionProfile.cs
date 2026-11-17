namespace Krosforge.Runtime.Native.Protocol.Connection;

/// <summary>External compatibility values loaded by the Host, never imported from Game.</summary>
public sealed record ConnectionProfile(
    int RequestEnvelopeField, int ResponseEnvelopeField, int CorrelationIdField,
    int ProtocolNegotiationOperation, int RpcVersion)
{
    public const int SupportedRpcVersion = 2;
    public void Validate()
    {
        int[] tags = [RequestEnvelopeField, ResponseEnvelopeField, CorrelationIdField, ProtocolNegotiationOperation];
        if (tags.Any(tag => tag <= 0 || tag >= 1 << 29)
            || RequestEnvelopeField == ResponseEnvelopeField || CorrelationIdField == ProtocolNegotiationOperation
            || RpcVersion != SupportedRpcVersion)
            throw new ProtocolException("Invalid connection compatibility profile.");
    }
}
