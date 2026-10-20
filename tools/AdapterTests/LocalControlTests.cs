using System;
using ThorsKist.Sync;
using UnityEngine;

namespace ThorsKist
{
    public sealed partial class AdapterTests
    {
        private void LocalControl()
        {
            int start = checks;
            var hello = new ZPackage();
            hello.Write(Wire.Protocol); hello.Write((byte)Wire.C2S.Hello);
            hello.Write(123L); hello.Write("local character"); hello.Write(new Vector3(1, 2, 3));
            int written = hello.GetPos();
            Check(written == hello.Size() && written > 0, "Native written control package starts at its end");
            Action<ZPackage> receiveHello = copy =>
            {
                Check(!ReferenceEquals(copy, hello) && copy.GetPos() == 0, "Local request gets independent reader at position zero");
                Check(copy.ReadInt() == Wire.Protocol && copy.ReadByte() == (byte)Wire.C2S.Hello
                    && copy.ReadLong() == 123L && copy.ReadString() == "local character" && copy.ReadVector3() == new Vector3(1, 2, 3)
                    && copy.GetPos() == copy.Size(), "Complete local Hello can be decoded exactly once per delivery");
            };
            JournalControlRpc.DeliverLocal(hello, receiveHello);
            JournalControlRpc.DeliverLocal(hello, receiveHello);
            Check(hello.GetPos() == written, "Local delivery/retry does not alter sender cursor");
            var header = new Hird { Id = "local-control", Name = "preserved hird", OwnerId = 123, Anchor = Vector3.zero };
            header.Members.Add(new HirdMember(123, "local character"));
            var reply = new ZPackage(); reply.Write(Wire.Protocol); reply.Write((byte)Wire.S2C.State); reply.Write(true);
            header.WriteHeader(reply); reply.Write(1); var epoch = Guid.NewGuid(); reply.Write(epoch.ToString("N")); reply.Write(0);
            JournalControlRpc.DeliverLocal(reply, copy =>
            {
                Check(copy.ReadInt() == Wire.Protocol && copy.ReadByte() == (byte)Wire.S2C.State && copy.ReadBool(), "Local state reply header can be decoded");
                var hird = Hird.ReadHeader(copy);
                Check(hird.Id == header.Id && hird.OwnerId == 123 && hird.IsMember(123) && copy.ReadInt() == 1
                    && Guid.ParseExact(copy.ReadString(), "N") == epoch && copy.ReadInt() == 0 && copy.GetPos() == copy.Size(),
                    "Local state reply preserves membership, epoch and roster framing");
            });
            Check(reply.GetPos() == reply.Size(), "Reply sender cursor remains at end");
            Logger.LogInfo("M7 LOCAL CONTROL PASS " + (checks - start) + " native assertions (listen-host request/reply cursor and retry)");
        }
    }
}
