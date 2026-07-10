using VirtualLan.Core.Net;
using VirtualLan.Core.Protocol;
using Xunit;

namespace VirtualLan.Core.Tests;

public class MacAddressTests
{
    [Fact]
    public void Broadcast_IsGroupAddress()
    {
        Assert.True(MacAddress.Broadcast.IsGroupAddress);
        Assert.True(MacAddress.Broadcast.IsBroadcast);
    }

    [Fact]
    public void RandomLocal_IsUnicastAndLocallyAdministered()
    {
        for (int i = 0; i < 100; i++)
        {
            var mac = MacAddress.CreateRandomLocal();
            byte first = mac.ToArray()[0];

            Assert.Equal(0, first & 0x01);  // I/G = 0 → unicast: nunca vira flood
            Assert.Equal(0x02, first & 0x02); // U/L = 1 → nunca colide com MAC de fabricante
        }
    }

    [Fact]
    public void Parse_RoundTrips()
    {
        var mac = MacAddress.Parse("02:AB:cd:EF:00:11");
        Assert.Equal("02:ab:cd:ef:00:11", mac.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("02:ab:cd:ef:00")]
    [InlineData("02:ab:cd:ef:00:11:22")]
    [InlineData("zz:ab:cd:ef:00:11")]
    public void Parse_RejectsGarbage(string text) => Assert.False(MacAddress.TryParse(text, out _));

    [Fact]
    public void EthernetFrame_ParsesHeader()
    {
        byte[] frame = new byte[64];
        MacAddress.Broadcast.WriteTo(frame);
        MacAddress.Parse("02:11:22:33:44:55").WriteTo(frame.AsSpan(6));
        frame[12] = 0x08; frame[13] = 0x06; // ARP

        Assert.True(EthernetFrame.IsValid(frame));
        Assert.True(EthernetFrame.GetDestination(frame).IsBroadcast);
        Assert.Equal("02:11:22:33:44:55", EthernetFrame.GetSource(frame).ToString());
        Assert.Equal(0x0806, EthernetFrame.GetEtherType(frame));
        Assert.Equal("ARP", EthernetFrame.DescribeEtherType(EthernetFrame.GetEtherType(frame)));
    }

    [Fact]
    public void EthernetFrame_RejectsRunts() => Assert.False(EthernetFrame.IsValid(new byte[13]));
}

public class MacTableTests
{
    [Fact]
    public void LearnsAndResolves()
    {
        var table = new MacTable();
        var mac = MacAddress.Parse("02:11:22:33:44:55");
        var node = NodeId.CreateRandom();

        Assert.False(table.TryResolve(mac, out _));

        table.Learn(mac, node);

        Assert.True(table.TryResolve(mac, out var resolved));
        Assert.Equal(node, resolved);
    }

    [Fact]
    public void NeverLearnsGroupAddresses()
    {
        var table = new MacTable();
        table.Learn(MacAddress.Broadcast, NodeId.CreateRandom());

        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void ExpiredEntries_AreNotResolved()
    {
        var table = new MacTable(TimeSpan.Zero);
        var mac = MacAddress.Parse("02:11:22:33:44:55");

        table.Learn(mac, NodeId.CreateRandom());
        Thread.Sleep(5);

        Assert.False(table.TryResolve(mac, out _));
    }

    [Fact]
    public void ForgetNode_RemovesAllItsMacs()
    {
        var table = new MacTable();
        var node = NodeId.CreateRandom();
        var other = NodeId.CreateRandom();

        table.Learn(MacAddress.Parse("02:00:00:00:00:01"), node);
        table.Learn(MacAddress.Parse("02:00:00:00:00:02"), node);
        table.Learn(MacAddress.Parse("02:00:00:00:00:03"), other);

        table.ForgetNode(node);

        Assert.Equal(1, table.Count);
        Assert.True(table.TryResolve(MacAddress.Parse("02:00:00:00:00:03"), out _));
    }

    [Fact]
    public void Relearning_MovesMacToNewNode()
    {
        var table = new MacTable();
        var mac = MacAddress.Parse("02:11:22:33:44:55");
        var first = NodeId.CreateRandom();
        var second = NodeId.CreateRandom();

        table.Learn(mac, first);
        table.Learn(mac, second);

        Assert.True(table.TryResolve(mac, out var resolved));
        Assert.Equal(second, resolved);
    }
}
