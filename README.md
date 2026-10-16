# questkit

A Quest Planner for VRChat avatars. It looks at a PC avatar, works out the best Quest version of
each part, shows you why, and lets you change anything before it builds a separate Quest copy.

The PC avatar is never modified. The only thing added to it is an editor-only component that
remembers your choices, and it is stripped on upload.

## Requirements

- Unity 2022.3 with the VRChat Avatars SDK 3.7 or newer (tested on 3.10)

## Install

Copy the `Editor` and `Runtime` folders into `Assets/QuestKit` in your avatar project.

## Use

1. Open **Tools > Quest Planner** and pick the PC avatar.
2. Press **Analyze**.
3. Pick a **target rank**. Very Poor is the default; Poor and above make the plan stricter.
4. Go through the plan. Every row shows the recommendation and the reason, and anything you change
   is marked and remembered.
5. Press **Apply**. A confirmation lists the meshes it will merge and any existing Quest texture
   sizes it will change, then it builds `<Name>_Quest` next to the PC avatar.

The header shows the projected value of each performance stat against the target, and which
limits make Quest remove things outright.

## What it decides

| Part | What it measures | Options |
| --- | --- | --- |
| Materials | Shader, how much of the texture is actually clear, brightness, emission | Toon Standard, Toon Standard (Outline), Toon Lit, MatCap Lit, Additive, Multiply, Drop |
| Textures | Surface area each texture covers, how much of it is on the head | 128 to 2048, or Auto |
| PhysBones | How many vertices each one moves, bone count, colliders | Keep, thin or remove |
| Contacts | Whether a receiver's parameter is used by any animator | Keep or remove |
| Triangles | Mesh detail, how much of each mesh is on the head, viseme/blink shapes | Target count, split across meshes |
| Meshes | Which meshes are toggled by the same things, which shape keys and bones nothing uses | Merge, strip blend shape normals, remove unused shape keys and bones |
| Atlas | Which materials share Quest settings, whether their UVs tile | Combine into one texture or not |
| Components | Lights, cloth, cameras, audio, physics, particles, Unity constraints, custom scripts | Remove or keep |

How it decides:

- **See-through materials:** Quest has no alpha blending. Bright or glowing parts become Additive,
  dark overlays (shadows, blush, lines) become Multiply, and the texture is baked to suit that
  shader. A material only marked transparent but with a solid texture converts to Toon Standard.
- **Texture sizes:** the budget comes from the 10 MB Quest download limit minus the meshes. Every
  texture starts small and the one with the most surface per pixel is doubled until the budget is
  spent. Face textures start at 1024. Sizes are set as Android overrides, so the PC build keeps
  full resolution.
- **PhysBones:** above the Poor limits Quest disables every PhysBone, even on a Very Poor avatar.
  The planner picks the set that moves the most of the mesh within those limits. A PhysBone with
  several parallel chains (hair strands, skirt panels) can be thinned: every other chain leaves
  the simulation and copies its neighbours through rotation constraints, which roughly halves its
  bones. Collision checks are shared out so each PhysBone first gets its most useful collider
  (floors first), and left/right colliders are only kept in pairs.
- **Contacts:** receivers that drive an animator parameter come first, then senders. Receivers
  nothing listens to are dropped.
- **Atlas:** Toon Standard materials with the same shading settings are packed into one texture
  (glow goes into a second, half-resolution one). Recommended when it costs no resolution, or when
  the target rank needs fewer material slots.
- **lilToon looks:** matcap, rim light and normal maps carry over to Toon Standard, shadows-off
  materials get a flat ramp, and extra main texture layers are baked into the base texture.
- **Animations:** the Quest copy gets its own controllers and clips wherever the PC ones would
  break: lilToon glow animation (remapped to Toon Standard), material swaps (pointed at the Quest
  materials) and curves on merged meshes. This covers the descriptor and Modular Avatar and
  VRCFury components.
- **Triangles:** a built-in simplifier collapses edges by quadric error. Surviving vertices keep
  their UVs, weights and shape keys; UV islands are never torn; viseme and blink vertices are
  locked; and no collapse may move the surface more than 0.4% of the mesh size, so a mesh stops
  short of its share rather than losing parts. The target is split so head meshes keep the most.
- **Unused shape keys and bones:** shape keys no animation, viseme, blink or shape changer uses are
  removed (ones turned up are baked in first). Bones that deform nothing and that no component,
  PhysBone or animation needs are dropped from the bone count and deleted.
- **Merging:** meshes are only grouped when the same toggles control all of them, so toggles keep
  working. A mesh whose material is swapped by an animation, or a second mesh with animated blend
  shapes, is left out. Merging also combines slots that share a material.

## Limits

- The download size is an estimate until you press **Measure real size**, which builds the Quest
  copy as an Android bundle (no platform switch). Modular Avatar and VRCFury don't run in that
  build, so what they add at upload isn't counted.
- Animated material properties other than lilToon glow only get a warning.

## License

MIT
