using HypaCore.Hybrid;
using HypaFirebase.Rest;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace HypaFirebase.Tests
{
    public class RestRoomSyncTests
    {
        [Test] public void Tree_FoldsPutsAndPatches_AsTheDatabaseSendsThem()
        {
            var tree = new RtdbTree();
            tree.Apply("put", "{\"path\":\"/\",\"data\":{\"alice\":{\"name\":\"Alice\",\"seat\":0,\"online\":true}}}");
            tree.Apply("put", "{\"path\":\"/bob\",\"data\":{\"name\":\"Bob\",\"seat\":1,\"online\":true}}");
            tree.Apply("patch", "{\"path\":\"/bob\",\"data\":{\"handUp\":true}}");
            tree.Apply("put", "{\"path\":\"/alice/online\",\"data\":false}");
            Assert.AreEqual("Bob", (string)tree.Root["bob"]["name"]);
            Assert.IsTrue((bool)tree.Root["bob"]["handUp"]);
            Assert.IsFalse((bool)tree.Root["alice"]["online"]);
            tree.Apply("put", "{\"path\":\"/bob\",\"data\":null}");
            Assert.IsNull(tree.Root["bob"]);
            tree.Apply("put", "{\"path\":\"/\",\"data\":null}");
            Assert.IsNull(tree.Root);
        }

        [Test] public void Tree_TurnsTheDatabasesArraysIntoKeys_SoAnswersAddUp()
        {
            var tree = new RtdbTree();
            tree.Apply("put", "{\"path\":\"/\",\"data\":[{\"text\":\"a\"},{\"text\":\"b\"}]}"); // keys 0 and 1 come as an array
            tree.Apply("put", "{\"path\":\"/2\",\"data\":{\"text\":\"c\"}}");
            Assert.AreEqual("c", (string)tree.Root["2"]["text"]);
            Assert.AreEqual("a", (string)tree.Root["0"]["text"]);
            tree.Apply("put", "{\"path\":\"/\",\"data\":{\"bob\":{\"h\":[0,1.6,0,0,0,0,1],\"t\":5}}}");
            Assert.AreEqual(JTokenType.Array, tree.Root["bob"]["h"].Type, "a pose's numbers stay an array");
        }

        [Test] public void ServerClock_UsesTheMidpoint_OfTheShortestRoundTrip()
        {
            var clock = new ServerClock();
            Assert.AreEqual(1000, clock.ServerNow(1000), "no sample yet: the device's own clock");
            clock.Add(1000, 1400, 51_000); // 400 ms round trip: offset 49 800
            clock.Add(2000, 2100, 52_000); // 100 ms round trip: offset 49 950, the better one
            Assert.AreEqual(49_950, clock.Offset);
            clock.Add(3000, 3900, 99_999); // slow and wrong: ignored while a faster sample is kept
            Assert.AreEqual(49_950, clock.Offset);
        }

        [Test] public void Players_ASilentPlayer_IsOffline()
        {
            var root = JToken.Parse("{\"a\":{\"name\":\"A\",\"seat\":0,\"online\":true,\"lastSeen\":1000}," +
                                    "\"b\":{\"name\":\"B\",\"seat\":1,\"online\":true,\"lastSeen\":9000,\"handUp\":true}," +
                                    "\"c\":{\"name\":\"C\",\"seat\":1,\"online\":true}}");
            var players = RestRoomSync.ParsePlayers(root, 1000 + RoomPresence.TimeoutMs + 1);
            Assert.IsFalse(players["a"].online, "nothing heard for more than 10 s");
            Assert.IsTrue(players["b"].online);
            Assert.IsTrue(players["b"].handUp);
            Assert.IsTrue(players["c"].online, "a client with onDisconnect sends no heartbeat");
            Assert.IsTrue(RestRoomSync.ParsePlayers(root, long.MaxValue)["a"].online);
        }
    }
}
