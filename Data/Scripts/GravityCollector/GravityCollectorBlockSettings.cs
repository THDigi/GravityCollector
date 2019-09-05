using ProtoBuf;

namespace Digi.GravityCollector
{
    [ProtoContract(UseProtoMembersOnly = true)]
    public class GravityCollectorBlockSettings
    {
        [ProtoMember(1)]
        public float Range;

        [ProtoMember(2)]
        public float Strength;
    }
}
