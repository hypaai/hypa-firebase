# Imported work and provenance

Imported locally on November 22, 2026, without Git history. The source folders were not modified. A fresh repository was initialized; no remote, commit or push was created during setup.

## Public baseline

- Source: https://github.com/hypaai/hypa-meta-vr
- Commit: `7c306b29c2b0543d859fb7006f324d0898d2dead` (upstream HEAD checked at initialization).
- Imported tracked working-tree files from the clean local checkout, using materialized Git LFS assets rather than unresolved pointers.
- Existing Stories/Lens/Core code, scenes, art, tests and documentation are imported work, including the elder stories, portal and room fixes. Retained Apache license, third-party notices and campfire attribution.
- Ignored files, caches, credentials, restricted media and Git history were not copied.

## Selected private flat-platform work

Source: `hypa-v2/apps/hypa-stories`, repository commit `fcfb1715e45b4492f3aaff7258c5905487d15676`. Selected paths were clean in the source checkout. The supplied G00 task records Chris's November 21 authorization to open-source this work under Apache 2.0.

Imported:

- `Assets/HypaCore/Editor/HypaBuild.cs` and metadata.
- `Assets/HypaCore/Editor/HypaCore.Editor.asmdef` XR-management references.
- `Assets/HypaCore/Quest/HypaCore.Quest.asmdef`, `Assets/HypaLens/Runtime/Quest/HypaLens.Quest.asmdef`, and the two `Assets/HypaStories/Quest` assembly definitions with flat-build exclusions.
- `Assets/HypaStories/Runtime/Ui/ScreenControls.cs` and metadata.
- `Assets/WebGLTemplates` and `Assets/HypaCore/Art` (app icons), with metadata.
- `tools/build.sh`.

Did not replace the public baseline's newer story, village, portal or room implementation with the older private copy. Build identifiers and scene selection were adjusted in this repository.

## Disclosure policy

Imported work is not claimed as new hackathon work, even where its original date falls within the submission period. Record this entry's changes in `docs/submission-work.md`. Preserve third-party licenses when adding assets or SDKs.
