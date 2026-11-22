# Hypa Firebase

- Never push code, publish, deploy, or create a remote repository without the user's explicit consent.
- Unity project stays at the repository root. Preserve existing `.meta` GUIDs.
- Keep Stories and Lens independent; shared gameplay/API code belongs in HypaCore.
- Firebase SDK adapters belong in HypaFirebase. Multiplayer behavior and networking remain undecided.
- Never ship a Hypa API key in client code, resources, build variables, or configuration. Use an authenticated server proxy with Firebase Auth and App Check.
- Keep restricted media, signing credentials and local Firebase configs out of Git. Preserve third-party notices and campfire attribution.
- Record imported work in docs/pre-existing-work.md and actual new work in docs/submission-work.md.
- Run appropriate checks; report which Unity/platform builds were actually verified.
