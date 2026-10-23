using System.Collections.Generic;

namespace SharedInventory;

internal sealed class ListReply
{
    public long RequestId;
    public string Error = "";
    public Dictionary<string, int> Qualities = new Dictionary<string, int>();
    public Dictionary<string, long> Items = new Dictionary<string, long>();
}

internal sealed class TransferReply
{
    public long RequestId;
    public string Error = "";
    public string ItemId = "";
    public long Amount;
    public long Total;
    public int Quality = 1;
}
