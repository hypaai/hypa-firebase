# Multiplayer — reserved boundary

No transport, SDK, room schema or gameplay behavior is selected yet. No multiplayer code runs.

Decide the first shared experience before choosing networking: shared story playback, presence, turn-taking, or something else. Define authority, participant limits, reconnect behavior, and Quest/phone/web compatibility first. Firebase Auth UID will identify participants; Firebase database rules stay deny-all until a schema and access policy are implemented and tested.

Firestore or Realtime Database may hold low-frequency room metadata/presence. Evaluate a dedicated transport only if real-time movement or voice is needed. Stories should consume a small session interface once these requirements exist, rather than depend directly on a networking vendor.
