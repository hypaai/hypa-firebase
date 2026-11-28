using UnityEngine;

namespace HypaStories.Quest
{
    // Compiled only when the Meta XR Core SDK v207+ is installed (asmdef defineConstraints: HYPA_META_XR).

    /// <summary>
    /// Quest input for Stories. Thumbsticks: left walks, right snap-turns (<see cref="StoryInput"/>).
    /// Raise a hand to ask: a tracked hand (open, not pinching) or a controller held above head height. Hands first, then
    /// controllers, like HypaCore's pointer. Hold time and re-arming are in <see cref="RaiseHandGesture"/>.
    /// Hands for the drum game: each tracked hand's palm (from its skeleton when there is one), else the controllers.
    /// </summary>
    public static class QuestStoryInput
    {
        const float AboveHead = 0.02f;
        static OVRHand[] _hands;
        static OVRCameraRig _rig;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register()
        {
            RaiseHand.Probe = IsRaised;
            StoryInput.Move = () => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            StoryInput.Turn = () => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch).x;
            StoryInput.Hand = HandAt;
        }

        /// <summary>
        /// Hand <paramref name="i"/>: 0 is the left, 1 the right. A tracked hand's palm, else that controller.
        /// Picked by handedness, not by the order the scene lists them, and only where a real hand can be: the rig can hold
        /// a second OVRHand that says it is tracked while it sits still at the rig's floor origin, over a metre below your head.
        /// </summary>
        static Vector3? HandAt(int i)
        {
            if (_hands == null || _hands.Length == 0 || System.Array.Exists(_hands, h => h == null))
                _hands = Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude);
            var anyTracked = false;
            foreach (var h in _hands) if (h != null && h.IsTracked) anyTracked = true;
            if (anyTracked)
            {
                if (i > 1) return null;
                var side = i == 0 ? OVRPlugin.Hand.HandLeft : OVRPlugin.Hand.HandRight;
                var head = Camera.main != null ? Camera.main.transform.position : (Vector3?)null;
                foreach (var hand in _hands)
                {
                    if (hand == null || !hand.IsTracked || hand.GetHand() != side) continue;
                    var at = Palm(hand);
                    if (head == null || (at - head.Value).sqrMagnitude < ArmReach * ArmReach) return at;
                }
                return null;
            }

            var c = i == 0 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            if (i > 1 || !OVRInput.GetControllerPositionTracked(c)) return null;
            if (_rig == null) _rig = Object.FindAnyObjectByType<OVRCameraRig>();
            var p = OVRInput.GetLocalControllerPosition(c);
            return _rig != null && _rig.trackingSpace != null ? _rig.trackingSpace.TransformPoint(p) : p;
        }

        /// <summary>No hand is further than this from the eyes, in metres.</summary>
        const float ArmReach = 1f;

        /// <summary>The middle of a tracked hand's palm, from its skeleton; the hand's own transform when it has none.</summary>
        static Vector3 Palm(OVRHand hand)
        {
            var sk = hand.GetComponent<OVRSkeleton>();
            if (sk == null) sk = hand.GetComponentInChildren<OVRSkeleton>();
            if (sk != null && sk.IsDataValid && sk.Bones != null)
            {
                // the two skeletons number their bones from 0 each (XRHand_Palm is the old wrist's number), so ask which one this is
                var palm = sk.GetSkeletonType().IsOpenXRHandSkeleton() ? OVRSkeleton.BoneId.XRHand_Palm : OVRSkeleton.BoneId.Hand_Middle1;
                foreach (var b in sk.Bones)
                    if (b != null && b.Transform != null && b.Id == palm) return b.Transform.position;
            }
            return hand.transform.position;
        }

        static bool IsRaised()
        {
            var cam = Camera.main;
            if (cam == null) return false;
            var headY = cam.transform.position.y + AboveHead;

            if (_hands == null || _hands.Length == 0 || _hands[0] == null)
                _hands = Object.FindObjectsByType<OVRHand>(FindObjectsInactive.Exclude);
            var anyTracked = false;
            foreach (var h in _hands)
            {
                if (h == null || !h.IsTracked) continue;
                anyTracked = true;
                if (h.IsDataHighConfidence && !h.GetFingerIsPinching(OVRHand.HandFinger.Index) && h.transform.position.y > headY) return true;
            }
            if (anyTracked) return false;

            if (_rig == null) _rig = Object.FindAnyObjectByType<OVRCameraRig>();
            var space = _rig != null ? _rig.trackingSpace : null;
            foreach (var c in new[] { OVRInput.Controller.LTouch, OVRInput.Controller.RTouch })
            {
                if (!OVRInput.GetControllerPositionTracked(c)) continue;
                var p = OVRInput.GetLocalControllerPosition(c);
                if (space != null) p = space.TransformPoint(p);
                if (p.y > headY) return true;
            }
            return false;
        }
    }
}
