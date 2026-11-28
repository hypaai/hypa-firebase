# Multiplayer

The two-player Story Circle (G09, HYP-204) runs on Firebase Realtime Database rooms. Its contract is `IRoomSync`
(`Assets/HypaCore/Runtime/Firebase/Interfaces.cs`, types in `Rooms.cs`), described in `docs/contracts.md` section 4, with rules
in `firebase/database.rules.json`. Implementations: `FakeRoomSync` (editor, tests), `RestRoomSync` (Quest, Android, iOS) and
`WebRoomSync` (WebGL). Stories uses the interface through `HybridServices.Current.Rooms` and never a Firebase type.

This folder stays reserved for P06: more than two players (`RoomSeats.MaxPlayers`), and a dedicated transport if real-time
movement or voice needs more than the Realtime Database gives.
