namespace VirtualLan.Core.Protocol;

public enum PacketType : byte
{
    /// <summary>node → relay: entra na rede, anuncia MAC e endpoints locais.</summary>
    Register = 0x01,

    /// <summary>relay → node: confirma entrada, atribui índice e devolve a lista de peers.</summary>
    RegisterAck = 0x02,

    /// <summary>relay → node: push de mudança na lista de peers.</summary>
    PeerUpdate = 0x03,

    /// <summary>node → relay: mantém a sessão e o mapeamento NAT vivos.</summary>
    Keepalive = 0x04,

    /// <summary>node → node: sonda de hole punching (também serve de keepalive no caminho direto).</summary>
    Punch = 0x05,

    /// <summary>node → node: resposta ao punch, confirma caminho direto bidirecional.</summary>
    PunchAck = 0x06,

    /// <summary>node → node: quadro Ethernet cifrado, caminho direto.</summary>
    DataDirect = 0x10,

    /// <summary>node → relay → node: quadro Ethernet cifrado, caminho de fallback.</summary>
    DataRelay = 0x11,

    /// <summary>node → relay: saída limpa.</summary>
    Disconnect = 0x20,

    /// <summary>relay → node: erro de protocolo.</summary>
    Error = 0x7F,
}

public enum ErrorCode : byte
{
    Unknown = 0,
    NetworkFull = 1,
    ProtocolVersionMismatch = 2,
    MalformedPacket = 3,
    NotRegistered = 4,
}
