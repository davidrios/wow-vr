# 0014: The player's own body in VR

**Status:** accepted (2026-11-05). Replaces the arms-only two-bone IK of M7.

## Decision

- **The body follows the headset and the hands the way VRChat's avatars do**, from those three points alone, with `VrBodyIk` in C#. Not Godot's IK modifiers: the solve needs WoW's skeleton conventions (no rest turns, bones found by key bone), the elbow taken from the wrist's turn, and hips that drop for a crouch, which are easier to keep right in a hundred-odd lines of our own than in a stack of modifiers. Skeletons run their modifiers by hand anyway (`CreatureNode`).
- **The body is scaled so its eyes are at the player's.** WoW's races run from a gnome's yard to a tauren's three, and the world's scale is the player's choice, so the body fits the player rather than the player the body. The standing height is the highest the eyes have been, rising slowly so tiptoe doesn't count. Only the player sees this; the server and others see the character at its own size.
- **The head and the shoulder pieces are hidden**, as in desktop first person: WoW's shoulders are big enough to fill the view from inside the body.
- **The hands go where XR Tools' hands are**, read from their wrist bones, which XR Tools tunes to how a controller is held. XR Tools' own hands are hidden while the body's are there, and shown for a model the solver can't use.
- **The body stands in the play space.** XR Tools moves its physics body under the head, so the feet can't say whether the player leans. The body's spot is kept in the play space, which the stick and turning move and the player's own movement doesn't, so leaning and bowing bend the body instead of moving it, until the head is further than the spine reaches, or the player stands up straight again.

## How it works

Each frame, after the walk or stand has posed the legs and the upper body is put back to standing:

1. The head's pivot goes where the eyes put it, turned as the headset is.
2. The hips drop (or, for a model that stands hunched, rise up to their rest height) so the spine, at its standing length, reaches the head: straight down for a crouch, less for a bow, as the head is further ahead. The legs bend to keep the feet where the animation put them.
3. The spine bends through the head's pivot (FABRIK, from standing bent a little back), and takes 45% of the head's turn from the body, more toward the top.
4. Each collarbone lifts for a hand above the shoulder and swings forward for one reaching ahead.
5. Each arm is a hinge. The elbow goes where the forearm would be for the wrist's turn, blended 70/30 with down and out, never up while the hand is below the shoulder, and never far toward the body's middle. The upper arm and forearm both keep the elbow's axis across the plane they bend in, the forearm takes half the hand's twist about it, and the hand turns as the real one.
6. Grip closes the fingers and thumb into the model's own fist, the trigger the index finger.

`--body-check --xr-player` poses a simulated headset and hands and checks it (`CLAUDE.md`).

## Known limits

- **Proportions.** Hands go where the player's are, so a body much wider than the player puts them inside itself: a tauren's hands meet in front of its belly with the forearms crossed, and a gnome's short arms are straight reaching forward. VRChat's avatars do the same. Spreading the hands to the body's shoulders would fix the first, if it's wanted after trying it in a headset.
- **Only simulated trackers so far.** XR Tools' wrist offset, the elbow blend, the lean slack and the standing-height guess all want a real headset.
