namespace MockRdp.Server;

/// <summary>
/// Phases of the RDP connection sequence (MS-RDPBCGR 1.3.1.1). Milestones fill these
/// in from the bottom up; anything past <see cref="TlsUp"/> is not yet implemented.
/// </summary>
public enum ConnectionState
{
    Initial,
    Negotiating,        // X.224 CR received, deciding security
    TlsUp,              // TLS established  -- end of M1
    McsConnect,         // M2
    McsChannelJoin,     // M2
    Licensing,          // M3
    CapabilityExchange, // M3
    Finalization,       // M3
    Active,             // steady state: graphics / input / channels (M4+)
    Closed,
}
