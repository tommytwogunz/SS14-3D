# SS14 FPS — mod log

Goal: Space Station 14 as a 3D first-person game, with Garry's Mod as the reference for feel
(movement, Source-style physics, physics gun). Build order (each stage working in the real game before the next):
1. Stations as 3D areas
2. World textures/sprites → lightweight 3D models
3. First-person camera + GMod-like movement
4. Source-style physics on objects and characters
5. Physics gun on the hotbar
6. Keep SS14 gameplay working

## Environment (2026-10-17)
- Windows 11, RTX 4070 Ti SUPER 16 GB, ~900 GB free on C:
- Installed: SS14 Playtest launcher (Steam), Garry's Mod (reference only), git
- .NET: only the 6.0 runtime. **SS14 needs the .NET 10 SDK** (`global.json`: 10.0.100, rollForward latestFeature)
- No Python on PATH (the `um` toolkit CLI needs it; not required for this project)

## Route — fork SS14 + RobustToolbox (chosen by user 2026-10-17)
- Upstream: github.com/space-wizards/space-station-14 (MIT code; most art CC-BY-SA 3.0, keep attribution)
- Engine: RobustToolbox submodule. Engine code is NOT sandboxed (only content assemblies are), so the 3D
  renderer and 3D physics go in the engine fork; game-specific bits go in content.
- Why: all SS14 gameplay (atmos, power, chem, jobs, antags) keeps working for free → stage 6 is mostly
  "don't break it". Cost: stages 2–5 are engine work.
- Online: own local server only (we run `Content.Server` ourselves). Never connect the fork to official servers.

## Architecture plan
- Units: 1 tile = 1 m in SS14. Wall height 2.5 m, eye height 1.6 m (GMod: 64 u eye ≈ 1.6 m at 1 u ≈ 2.5 cm).
- Simulation stays authoritative on the server in SS14's 2D (x, y). A new height axis (z) is added as a
  component for things that need it (stage 4), so existing systems keep working on (x, y).
- Stage 1 renderer: a 3D view in the client that builds meshes from map grids
  - floor tiles → textured quads at z = 0, ceiling at z = 2.5
  - wall entities (airtight + occluder) → extruded boxes using the wall sprite as texture
  - every other sprite entity → camera-facing billboard (placeholder until stage 2)
  - lights: SS14 PointLight → simple per-pixel point lights
- Stage 3: mouse-look camera; WASD relative to view yaw; client-side prediction already exists in SS14.

## Versions
- space-station-14 master @ 614f030 (2026-10-17), RobustToolbox @ de49cf7 (server reports 293.0.1.0)
- Work branch `fps3d` in BOTH repos (`ss14/` and `ss14/RobustToolbox/`). Nothing committed yet.

## How to run (dev loop)
- Server: `ss14/bin/Content.Server/Content.Server.exe --cvar game.lobbyenabled=false --cvar game.map=Box --cvar game.defaultpreset=extended` (log → `logs/server.log`)
- Client: `tools/client.sh` (connects to localhost as "Tester" with `render3d.enabled=true`, log → `logs/client.log`)
- `tools/rebuild_client.sh [client args]` = kill client by PID, build engine+content, relaunch, wait until in game
- `tools/cmd.ps1 -Command "<console cmd>"` types into the dev console (backtick key). Cvars need the `cvar` prefix.
- `tools/shot.ps1 -Out <png>` screenshots the client window
- In game: `fps3d [on|off]`, `fps3d_look <yaw> [pitch]`, `fps3d_dump` (logs one frame's entities/grids),
  `cvar render3d.debug_mask N` (1 floors/ceilings, 2 entities, 4 height-colour debug)
- Mouse is captured while the 3D window is focused; hold Left Alt (or open chat/console) to free the cursor.

## Stage 1 — stations as 3D areas: DONE (verified in game 2026-10-17)
Code:
- Engine `Robust.Client/Graphics/Clyde/Clyde.World3D.cs`: 3D pass replacing the 2D world pass for the viewport
  looking through the current eye. Floors + ceilings meshed per grid (cached, rebuilt on `LastTileModifiedTick`),
  entities from the sprite tree each frame: Box (walls/windows/closed doors), Flat (draw depth ≤ HighFloorObjects),
  Billboard (everything else, RSI direction chosen relative to camera yaw). 32 nearest PointLights, per-pixel, no shadows.
- Engine `Robust.Client/Graphics/World3D/World3DManager.cs` (IoC `IWorld3DManager`): yaw/pitch + content shape hook.
- Engine `Robust.Shared/CVars.cs`: `render3d.*` cvars. Hook in `Clyde.HLR.cs RenderViewport`, init in `Clyde.cs`.
- Content `Content.Client/Fps3D/Fps3DSystem.cs`: mouse-look (SDL relative mode), classifier (DoorComponent,
  IconSmooth key walls/windows, Window tag), console commands.
What SS14/Robust really does (gotchas):
1. Clyde enables GL primitive restart (index 65535) globally for its 2D quad batches. On NVIDIA it also applies to
   non-indexed `glDrawArrays`, so any mesh > 65535 vertices gets its triangle assembly shifted by one vertex.
   Symptom: huge spiky triangles joining floor and ceiling. Fix: disable restart during the 3D pass, re-enable after.
   (Found by GPU readback: the buffer matched the CPU copy exactly, but the draw didn't.)
2. Station grids are rotated in world space (Box: ~205°). Anything axis-aligned must use the entity/grid world rotation.
3. There is no `Wall` tag (TagSystem asserts on unknown tags). Walls are recognised by IconSmooth key "walls".
4. SDL relative mouse mode reports one big jump when it turns on → ignore motion for 3 frames after capture.
5. Engine code is not sandboxed; content can use `IClyde.MouseMove`, `IClydeWindow.SetRelativeMouseMode`.
6. WASD is still world-relative (N/S/E/W). Movement is rotated by `InputMoverComponent.RelativeRotation`
   (networked, server-authoritative) → stage 3 must send the camera yaw to the server.
Known stage-1 limits (for later stages):
- Mouse clicks still hit the 2D world under the cursor; need crosshair raycast interaction (stage 3).
- Lights have no shadows/occlusion and leak through walls.
- Wall-mounted items sit inside wall boxes; directional windows render as full boxes; tables are billboards (stage 2).
- No space/parallax background (black void outside the station).

## Stage 2 — sprites/textures → lightweight 3D models: DONE (verified in game 2026-10-17)
Approach: no hand-made assets. Models are generated at runtime from the game's own sprites, so every
entity (thousands of RSIs, including future upstream ones) gets a model automatically.
- `Clyde.World3DModels.cs`: sprite-frame extrusion. Each atlas texture's alpha is downloaded once
  (`GetTexImage`; GL rows are bottom-up). Each opaque pixel (alpha ≥ 128) is extruded to a depth; front/back
  are single alpha-tested quads, side faces are merged runs whose UVs sample the edge pixels (colour-correct
  sides). Meshes are cached per (texture, frame rect, depth) in one append-only VBO and drawn per entity with a
  model matrix + tint uniform.
- Shapes (`World3DShape`), from content classifier first, then engine draw-depth heuristics:
  Box (walls, full windows), Prop (machines, lockers, chairs: front frame extruded 0.7 m, rotated with the entity),
  Mob (0.22 m thick, faces the camera, frame by view angle), Item (lies flat, sits on a table if the tile has one),
  Table (top-down sprite as a 7 cm slab at 0.9 m + pedestal), WallMount (panel on the face of the adjacent/own
  wall tile, 2.0 m if it has a PointLight else 1.25 m), Pane (directional windows on the local south edge),
  Door / Fence (full-height panel along the wall line, 12 cm / 3 cm), Flat, Billboard (gases, effects).
- Upright models are stretched by `render3d.upright_scale` (1.75): SS14 sprites are 3/4-view, so height is
  foreshortened; 1.75 puts humans at ~1.6 m against 2.5 m walls and a 1.6 m eye.
- Perf: 60 FPS (vsync cap) in Box bar/medbay, ~53 KiB/frame alloc, debug build.
Verified: arrivals, bar (stools, jukebox, arcades, shotgun lying on a table, card table), medbay desk
(items on counter, glass panes, wall sign). Dev command `fps3d_tpgrid x y` teleports to grid coords from map files.
Box beacons (grid coords): bar 23.5,-6.5 · kitchen 35.5,-5.5 · medbay 30.5,-17.5 · bridge -1.5,-7.5.
Stage 2 polish (2026-10-17, verified in game):
- Wall sides are split into 1 m segments so the 1 m wall sprite repeats instead of stretching over 2.5 m.
- Doors: always shape Door. Content hook `IWorld3DManager.DoorOpenAmount` returns 0..1, eased toward
  DoorState (Open/Opening → 1) over the door's Opening/ClosingAnimationTime. The engine draws two half-panels
  that slide 0.5 m each into the neighbouring walls, always textured with the RSI's "closed" state (other
  door-cycle frames are top-down art). Overlay layers only show while fully shut; fully open doors are hidden
  (firelocks are open by default and may have no walls to hide in). `fps3d_doortest <0..1|-1>` forces it.
Known stage-2 limits: mobs are thick cut-outs, not rigged models; props seen from behind show the mirrored
front art; door open timing is client-eased, not synced to the server's NextStateChange.

## Stage 3 — first person, GMod-style movement and interaction: DONE (verified in game 2026-10-17)
- Mouse-look (stage 1) + **WASD relative to the camera**: `SharedFps3DLookSystem.ApplyYaw` sets
  `InputMoverComponent.RelativeRotation` and `TargetRelativeRotation` (both, so the mover doesn't lerp) to
  `yaw − worldRot(mover.RelativeEntity)`, and turns the mob to face the look (entity rot = yaw + π, 0 faces south).
  Client predicts it every frame and sends `Fps3DLookMessage` (≤30 Hz, on change); the server applies it for the
  sender's attached entity. Needs SS14's relative movement on (default).
  Verified: looking west, forward moved −X until a closet blocked; looking north, forward moved +Y to the wall.
- **Crosshair interaction**: the renderer records an oriented pick box per drawn entity (shape-specific; doors
  more than half open are click-through). `Viewport.LocalToWorld` on the 3D viewport ray-casts (through the cursor,
  or the screen centre when `IWorld3DManager.CrosshairMode`) and returns the hit entity's position or the floor
  point. Every mouse-aimed system goes through that, so clicks, E-use, melee, guns and throwing aim at the crosshair
  without per-system changes. `GameplayStateBase` uses `CrosshairEntity` directly for the clicked entity / hover.
  `Viewport.WorldToLocal` projects through the 3D camera (at 1.2 m), so popups and speech follow entities.
- While captured, the engine pins the logical cursor at the window centre (`WindowReg.RelativeMouse` in SDL motion
  handling), so UI hover and clicks don't land on hidden HUD widgets.
- Capture releases with Left Alt (GMod context-menu style), text focus, or any open window/popup.
- HUD: `Fps3DCrosshairOverlay` "+" and target name within 6 m (white when within ~1.5 m reach). The theme default
  font draws nothing in SS14; load NotoSans via `IResourceCache` like other content overlays.
- Verified: E (ActivateItemInWorld) on a fire closet opened it (the player could then walk into it), and left-click
  (Use) closed it with the player inside, SS13-style.
- Dev commands: `fps3d_crosshairtest 1`, `fps3d_key <KeyFunction> <down|up>` (an input command built like a real
  key press, with the crosshair target), `fps3d_where`.
Not yet (needs stage 4 height physics): jump, crouch, falling. SS14's own run/walk toggle stands in for GMod sprint.

## Stage 4 — Source-style physics (height, gravity, jump, crouch, throws, knock-overs): DONE (verified 2026-10-17)
Route: a 2.5D height layer on top of SS14's own 2D rigid-body physics (mass, friction, momentum, pushing stay
SS14's), instead of a separate 3D engine that every SS14 system would have to be reconciled with.
- `Content.Shared/Fps3D/Height3DComponent.cs` (networked): Kind (Mob/Item/Prop/Table), Z, VZ, Height,
  Crouching, Tilt/TiltVelocity/TiltDirection, Spin, LookYaw, LookPitch.
- `SharedHeight3DSystem`: gravity 15.2 m/s² (GMod 600 u/s²), jump 5.4 m/s (≈0.96 m peak), crouch eye 1.0 m and
  ×0.5 speed, mob step-up 0.35 m (Source 18 u ≈ 0.46). Ground = floor or the highest table/prop top within
  step-up of the feet (EntityLookup radius 0.3 mobs / 0.12 items). Items bounce (×0.3 above 2.5 m/s) and tumble
  while airborne. Weightless → no falling, Z drifts. Client predicts only its own body; others come from state and
  are eased in the renderer's pose provider.
- Height-aware collisions: `PreventCollideEvent` cancels a pair when one body is entirely above the other's top
  (mobs get their step-up as margin against tables/props, so they snap onto them). Bodies without a height
  (walls, machines, airlocks) always block. **Gotcha: Robust filters contacts only on creation**, so whenever Z
  changes the system calls `SharedBroadphaseSystem.Refilter` on the body's fixtures.
- Throws: `ThrownEvent` → start at thrower Z + 1.25 m, VZ = speed·tan(look pitch) + 1.2 (lob).
  Tip-over: `StartCollideEvent` on a Prop ≥ 1 m tall when relative speed ≥ 6 m/s and speed×mass ≥ 25.
- Heights are attached by a 1 s server sweep (mobs via InputMover, items, anchored PlaceableSurface = tables,
  unanchored EntityStorage = lockers 1.7 m / crates 0.8 m). **Gotcha: the event bus allows one subscription per
  (component, event)**: other systems own MapInit on those components (subscribing crashed server startup).
  **Gotcha: lockers also have PlaceableSurface**; only anchored surfaces are tables.
- Look yaw is stored on the component and re-asserted every tick: SS14's mover snaps its relative rotation to a
  cardinal when you change grids (e.g. teleport off the arrivals shuttle), which otherwise skews WASD.
- Keys: `Jump3D` = Space, `Crouch3D` = Control (human context; Space was only bound in shuttle/arcade contexts).
- Engine: `IWorld3DManager.PoseProvider` (Z, tilt about the base, Known flag) applied to props/mobs/items and pick
  boxes; `CameraHeight` = body Z + eased eye offset (1.6 stand / 1.0 crouch / 0.4 lying). Knocked-down/dead mobs
  (StandingState) render lying down.
Verified in game (numbers from `fps3d_where`/`fps3d_track`): jump 0 → 0.87 → 0 m with camera 1.6 → 2.47 m;
crouch camera 1.0 m; run+jump onto a carpet table → z 0.90, across it and off the far edge, from both sides;
mapped toolboxes on tables start at z 0.90; thrown toolbox 1.49 → 1.80 m peak → bounce → rest, spinning;
toolbox thrown at a locker tipped it 0 → 0.12 → 0.36 → 0.95 → 1.57 rad, rendered falling and lying.
Known limits: a tipped prop keeps its upright 2D footprint; fallen props can't be stood back up yet (stage 5
physgun); items don't stack on items; no fall damage; mobs can't stand on other mobs.
Dev: `fps3d_track <proto>`, tools `test_tablejump.sh`, `test_knockover.sh`, `goto.sh`, `restart_all.sh`.
Note: relaunching the only client restarts the round (dev server, lobby off), so coordinates and entity IDs change.

## Stage 5 — GMod physics gun: DONE (verified in game 2026-10-17)
- `PhysGun` item (Resources/Prototypes/Entities/Objects/Tools/physgun.yml; tesla gun sprite tinted #8fd3ff,
  CC-BY-SA) handed to every player on `PlayerSpawnCompleteEvent`, plus hotbar action `ActionPhysGun`
  (`PhysGunSummonActionEvent`): makes the gun's hand active, takes it out of bags, or spawns a new one.
- Controls (only while the gun is the active-hand item; otherwise keys behave normally): hold left mouse = grab
  the crosshair target, mouse wheel = distance, E + mouse = rotate (yaw spin + tip), right-click = freeze
  (body made Static, height layer Frozen; re-grabbing unfreezes; mobs can't be frozen), release = drop with
  the swing's momentum. Anchored/world things can't be grabbed (like GMod).
- Server `PhysGunSystem` drags the held entity to eye + look·distance with a stiff spring through SS14 physics
  (SetLinearVelocity, so it still collides) and drives Height3D Z/VZ directly. Client `PhysGunInputSystem`
  sends Grab/Release/Freeze/Scroll/Rotate messages.
- Visuals: glowing camera-facing beam strips (`IWorld3DManager.Beams`, others' beams from their hand) and a
  first-person view model of the active-hand item (`ViewModelEntity`, drawn after clearing depth).
- **Gotcha: SS14's DragDropSystem also binds left mouse before interaction**; it holds presses back to detect
  drags and swallows quick releases. Bind before `DragDropSystem` too or grab happens on release and release
  is lost. **Gotcha: arrivals docking knocks unbuckled passengers down, which drops held items**; the hotbar
  action recovers the gun. Pressing ToggleKnockdown twice cancels the stand-up do-after.
- Engine: `IClyde.MouseWheel` made public; picking ignores boxes containing the camera; in 3D,
  `EyeManager.GetWorldViewbounds` returns a draw-distance box (screen corners made an inverted Box2 →
  debug assertion crash when the context menu ran).
Verified (`test_physgun.sh`): grab lifts a crate to 1.2 m, scroll 0.9 → 1.8 m, rotate tilt 0.6, freeze holds it
mid-air after looking away, re-grab unfreezes, flick release carried it 1.55 m and it fell to the floor; hotbar
action re-equipped a dropped gun; a mob (Pun Pun) was grabbed, lifted and swung.

## Texture/visual cohesion pass (2026-10-17, after user feedback "ugly and uncohesive")
Causes found: walls stretched SS14's top-down corner fragments (solid0..7) up 2.5 m faces; ceilings reused each
floor tile; extruded sides sampled the sprites' 1 px black outline; no mipmaps (shimmer); flat murky lighting.
- `Clyde.World3DMaterials.cs`: per wall type, a generated 32×32 (1 m) pixel-art panel in the average colour of
  the RSI's `full` icon (outline-weighted), with seams, bevel, belt, rivets, ±4% dither; glass variant when the
  icon is mostly translucent; one shared ceiling panel (ceilings drawn as a second range of the grid mesh).
- Mipmaps (NearestMipmapLinear; max level 3 for atlases to limit bleeding, 5 for materials) on every texture
  the 3D pass binds.
- Model sides sample the first solid pixel up to 2 px inside the outline.
- Sprite pixels are read per rectangle with `glGetTextureSubImage` (the RSI atlas can be 12288², the old full
  download was ~576 MB).
- Shader: hemisphere ambient (sky 0.20/0.21/0.25, ground 0.07), smooth windowed light falloff with wrap,
  exposure tonemap (1.6), 48 lights.

## Stage 6 — SS14 gameplay in first person (verified in game 2026-10-18)
`tools/s6.sh [test ...]` runs each check on a fresh round (`restartroundnow`) in its own room, acting only
through the game's input path (`fps3d_key` = the input command a real key/click sends, aimed by the 3D
crosshair) and checking the exact entities involved. Results (full run):
- tools: wirecutters cut a grille · construction: steel on a girder → solid wall · combat: unarmed punches
  (15 dmg) · guns: racked Mk58 hits a human (0 → 48) · medical: bruise pack heals · chemistry: beaker pours
  5u into another · atmos: gas analyzer UI · power: APC UI (ApcMenu) · doors: airlock opens when walked into ·
  jobs/access: a locked engineering airlock denies the passenger and opens after `setoutfit … StationEngineerGear`
  · PDA: PdaMenu from the ID slot (shows Station Engineer) · UIs: crafting/character menus, examine (shift-click),
  context menu (right-click).
- 12/13 on the first full run; chemistry failed once there (cause not captured; most likely the unverified pour aim) and passed 4/4 alone. The pour
  now uses a crosshair-verified aim with one retry.
- Not covered: the inventory menu key (OpenInventoryMenu opens no window in this HUD layout; slots are on the
  bottom bar), surgery, cargo, botany, antag roles, and real multi-player rounds.
Bugs found and fixed:
- Clicks on draggable things (beakers, chairs, bodies) turned into drags: real clicks carry the raw cursor
  position while `MouseScreenPosition` is the crosshair, so DragDropSystem saw a phantom move past its deadzone.
  `GameplayStateBase` now reports crosshair-mode clicks at `MouseScreenPosition` (and `fps3d_key` does too).
  The harness drives `fps3d_key`, so the GameplayStateBase path itself still needs a real-mouse check.
- Melee/guns aimed at the OS cursor: `Clyde.MouseScreenPosition` returns the window centre in crosshair mode.
- Gas analyzer window crashed debug builds: `StripeBack.Draw` built inverted boxes before first layout (guard).
- Fences/grilles were hard to target edge-on: pick volume thickened to 0.15 m.
Harness notes:
- After a round restart you spawn on the arrivals shuttle; `fps3d_tpgrid` now converts against the largest grid
  on the map (the station). Stations can spawn rotated: `fps3d_track` prints `grot=` and `near()` steps out
  along the grid axes.
- `near <proto> <aimZ> [dist]`: stands on each grid side of the nearest few targets (two aim heights, to see a
  wall-mounted APC over a locker) and keeps the one where the crosshair lands on that exact entity.
- New dev commands: `fps3d_slot <slot>` (click an inventory slot item, e.g. the PDA in `id`), `fps3d_action`,
  `fps3d_inspect`, `fps3d_ui`, `fps3d_closeui`. Source `s6.sh` with `S6_LIB=1` to use the helpers by hand;
  `S6_DEBUG=1` shows what `near()` sees.
- SS14 facts: reach 1.5 m; fresh pistols need racking (ActivateItemInHand); bottles are sealed (use beakers);
  hotbar slots vary by job (`fps3d_action CombatMode`); monkeys wander (use MobHuman); admin commands take
  NetEntity numbers.

## Movement smoothness pass (2026-10-18, after play-test feedback "movement feels very shaky")
Measured with `fps3d_camlog <s> [v]` (per-frame camera position; `v` dumps every frame) via `tools/walktest.sh`
and `tools/camtest.sh`. Before: Debug build at ~26–33 fps with 70–100 ms hitches, many frames where the camera
didn't move at all ("stalled") and frames where it jumped back (server corrections), even with a steady view.
Causes and fixes:
- Look direction was a separately timed network message, so the server walked you along a slightly older
  heading than the client predicted, and each server state snapped the camera back. `Fps3DLookMessage` is now a
  predictive event (`RaisePredictiveEvent` in the client tick, `SubscribeAllEvent` in the shared system): the
  server applies it at the same tick, and the client re-applies it when replaying.
- The client wrote the body rotation every *frame* (outside ticks), which re-seeded the engine's transform
  lerp and froze the camera between ticks. Now yaw is only applied inside ticks.
- The camera interpolates the player's end-of-tick positions (and height) itself by the tick fraction
  (`Fps3DCameraTickSystem` samples after physics; engine hook `IWorld3DManager.CameraPosition`).
- Debug builds are too slow for play: the scripts now build Release by default (`CONFIG=Debug` to override).
  Release needs `partial` on systems with `[Dependency]` and the non-generic `TryComp` for transforms.
After: Release at ~60 fps, max frame 37 ms over a 1 s walk, two evenly sized camera steps per 30 Hz tick, and
0–1 backward steps while wobbling the view (was 5–11).

## Real 3D models (2026-10-18, after play-test feedback "the sprites look ugly")
Common entities now draw real meshes instead of extruded sprites; everything else keeps the sprite model.
- **Authoring:** `ss14/Tools/models3d` (Blender 5.1, headless: `blender -b --factory-startup -P
  Tools/models3d/build.py -- [names]`, ~2 s per model). It's a small modelling toolkit (bevelled boxes,
  cylinders, spheres, slats, decal quads) with per-part tint, bone and visibility group. It joins the parts,
  smart-UV-unwraps them and Cycles-bakes base colour (paint fade, grime, darker creases, scuffed edges,
  scratches) x ambient occlusion. Output goes to `Resources/Models3D/<name>.json` (triangles per part) and
  `.png`. Models: human, locker, crate, vending, computer, chair, armchair, officechair, stool, bed,
  canister, plant.
- **Colours come from the game:**
  - Tint `p0..p2`: the entity's own sprite palette (top 3 colour families, outline excluded), so an
    engineering locker is yellow and a plasma canister orange.
  - Tint `c0..c7`: content colours, a named sprite layer (`ColorLayers`) or another entity's sprite
    (`ColorEntities`, e.g. a worn backpack).
  - Tint `s0`/`sN`: a sprite decal (`uUVRect` shader uniform) cropped to the frame's opaque pixels:
    vending fronts show the real machine art, and console screens show the live screen layer.
- **Engine:**
  - `Clyde.World3DMeshes.cs` and `IWorld3DManager.ModelProvider` → `World3DModelRequest` (model, colours,
    bones, hidden groups, scale, yaw).
  - Meshes share the model VBO and draw per part.
  - The click volume is the mesh bounds.
  - Loaded textures must be kept referenced: an unreferenced OwnedTexture is freed by its finalizer. That
    made the 3D pass throw every frame.
  - Robust textures are bottom-up, so exported UVs are not V-flipped (`m3d.V_FLIP = 0`).
- **Content:** `Fps3DModelSystem` maps by prototype ancestry (`EnumerateAllParents`, since most bases are
  abstract). The closest listed ancestor wins, and benches are excluded.
  - Lockers swing their door when open and show a lock light; crate lids open.
  - Characters are any mob with the humanoid Chest layer:
    - Colours: skin from the Chest layer, torso from outerwear or jumpsuit, legs from the jumpsuit, plus
      shoes, gloves (hands), hair, and hat and backpack from the worn items.
    - Slot and hair keys are empty bookmark layers, with the drawn layer right after them.
    - The walk cycle swings the limbs with speed, and the head follows the look pitch.
- **Harness:**
  - `fps3d_tpstation` (server) teleports on the real station grid. The client only knows grids in view
    range, and asteroids can be bigger than the station.
  - `tools/look.sh` screenshots a model.
  - `fps3d_layers <uid>` lists the mapped sprite layers.
  - `forcemap Box` if a round loads elsewhere.
- **Verified:** the gameplay suite still passes 13/13 with models. Screenshots in `logs/s6_*.png` (humans,
  lockers, chairs, computer, canister, vending, plants).
- **Not done yet:** tables (still sprite slabs), machines/lathes, beds and crates not visually checked,
  benches, non-human mobs, hats beyond a generic cap.

### Second batch: tables, machines, benches, animals, rugs (2026-10-18)
- **New models:**
  - **table:** the surface sits at 0.9 m, where the game places items. Each corner leg is its own group,
    hidden when any of the three tiles sharing that corner holds a table, so a run of tables is one surface
    with outer legs.
  - **bench:** slatted, with a backrest.
  - **machine:** a panelled cabinet with the machine's sprite as the front decal. Used for lathes, chem
    master/dispenser, SMES, substation, R&D server, cloning pod and medical scanner.
  - **quadruped:** corgi, cat, fox, pig, possum, raccoon, goat, cow, mouse, hamster and lizard, scaled per
    species.
  - **bird:** chicken and duck.
  - **fish:** space carp.
- **Animation:** animals walk with diagonal leg pairs, wag their tails and flap wings; carp sway. Monkeys
  use the human model at 0.6 scale.
- **Model table:** `(Base, Model, Scale)`; the closest listed ancestor wins.
- **Rugs** ("they look weird"): the corner-smoothed carpets (red, green...) showed as broken half-tile
  strips with floor between them, and patterns were split. Flat sprites must follow Robust's 2D maths
  (`SpriteSystem.Render`):
  - The 2D eye turns with the grid, so the direction comes from the rotation relative to the grid.
  - The quad turns by the sprite rotation (minus the cardinal snap) minus the angle of the
    *pre-offset* direction.
  - The frame uses the direction *after* the override and the layer's `DirOffset`. Corner-smoothing layers
    pick their corner through `DirOffset`.
  - `GetTopDownFrame` does this for flat sprites and items on the floor.
  - The chapel carpet's black cross lines are in its art (tgstation sprite), not a bug.
- **Shader:** sampled colours are un-premultiplied (divided by alpha), so minified sprite edges don't darken
  towards the transparent atlas border.
- **Harness:** `tools/scene.sh <gx> <gy> <shot> <protos...>` spawns a row and screenshots it. `EXACT=1` makes
  `aim.sh`/`aim_clear`/`near` match the prototype exactly ("Beaker" also matched a nearby BoxBeaker).
- **Verified:** gameplay suite 12/13 on the full run; chemistry failed on that BoxBeaker mix-up and passes 2/2
  with exact matching.

## Test harness (no keystrokes)
- Test launches pass `render3d.mouse_capture=false`: a relaunched client steals focus and would otherwise
  capture the user's mouse (and their mouse movement turned the test camera).
- `fps3d_closeui` closes windows/popups; `tools/test_physgun.sh` starts with rejuvenate + bar teleport +
  hotbar action. `tools/aim.sh <proto>` aims at the nearest entity of a prototype.
- 2026-10-17 incident: SendKeys-based console typing desynced (console already open), typed text into game chat
  and, with the user active in another app, a screenshot captured their Discord window (deleted). Never drive
  the keyboard/focus while the user may be at the PC.
- Replacement: engine `World3DDevBridge` (cvar `render3d.dev_cmd_file`, set by `tools/client.sh`) runs console
  commands written to `logs/cmd.txt` each frame; `fps3d_shot "<abs path>"` saves a game-only PNG via
  `IClyde.Screenshot`. Use `tools/run.sh "cmd" "cmd" ...`. Quote paths (the folder name has a space).
- `cmd.ps1` / `shot.ps1` (keystrokes + screen copy) are retired; don't use them.

## Log
- 2026-10-17: recon done, route chosen, cloning upstream into `ss14/`.
- 2026-10-17: stage 1 renderer built; primitive-restart bug found and fixed; rotated walls fixed; verified in game.
- 2026-10-18: stage 6 gameplay pass (tools/s6.sh), see the stage 6 section.
