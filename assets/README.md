# Einherjar model and icon sources

The original models are authored in [ContentModels.cs](../src/ContentModels.cs).
That deterministic C# generator is the editable model source; no Unity editor,
asset bundle, Blender installation, or Jotunn dependency is required. No game
meshes or textures are distributed. Materials clone the installed game's
`Custom/Piece` wood material and replace its main texture with a runtime white
texture. The snow-overlay renderer must not be used as a material source.

Coordinates use metres, Y up, front at negative Z. The chest floor pivot is
`(0, 0, 0)`, its silhouette is approximately 1.98 x 1.36 x 1.02 metres, and its
simple box collider is 1.98 x 1.28 x 1.02 metres, centred at Y=0.64. The crystal
is 0.46 metres tall with a matching pickup collider. Both models have one
renderer and four shared material slots. Geometry and materials are generated
once during registration and shared across instances, placement ghosts, and
scene changes. Plugin teardown releases owned meshes, materials, sprites, and
textures. Neither model has an Update script or a running particle system.

`chest.png` and `essence.png` are 256 x 256 transparent renders of those actual
models in Valheim's Unity 6000.0.75f1 runtime. They are embedded in
SharedInventory.dll and loaded once; release builds do no camera rendering.
The chest silhouette and inset stone match its hammer icon, and the crystal
rune matches its inventory/recipe icon. All generated geometry and captured
artwork are original repository assets; there are no downloaded asset licenses
or redistributed extracted game textures to manage.

Dimmed Essence reuses the crystal mesh with a separate violet crystal material;
the regular Essence and chest retain their amber material. Its inventory icon
is a one-time purple palette variation of the embedded `essence.png`, preserving
the rendered geometry, neutral rune/highlights, and alpha. Headless registration
creates neither variant's render assets.

To reproduce after changing a model:

1. Prepare a marked isolated game with the isolation helper as described in
   [WINDOWS-CODEX-SETUP.md](../WINDOWS-CODEX-SETUP.md). Enable the test harness,
   leave impairments off, and set the helper's FreeBuild=false.
2. Run `tools/Test-Assets.ps1 -TestRoot '<isolated root>' -Mode Review`.
   It builds the testing DLL and creates/loads only its local EinherjarAssets
   character and world. It resets that test character's essence, equips a
   hammer, and exercises pickup, requirements, ghost, placement, interaction,
   and destruction. Inspect the models in the world and the small UI icons.
3. After visual review, close the test game normally and run the same script
   with `-Mode Capture`. This exits after exporting `asset-capture/chest.png`
   and `asset-capture/essence.png` beneath the isolated root. In Review mode,
   F6 also recaptures the icons. Both assets use the same lighting, camera angle,
   and proportional framing. Separate temporary stages prevent overlap.
4. Copy those two PNGs to this directory, rebuild, and repeat Review. It checks
   for visible alpha coverage as well as transparent margins. Valheim's piece
   shader writes zero alpha; the development capture reconstructs coverage from
   black/white background renders and removes premultiplication.
5. Close the review game and run `-Mode Headless`. This launches the guarded
   runtime with `-batchmode -nographics`, checks server-role registration without
   generating meshes, materials, icon textures, or an asset camera, writes
   `headless-acceptance.txt`, and exits. Review reports
   go to `asset-acceptance.txt`; logs go to `asset-<Mode>.log`.

The script uses the same automatic .NET 8 SDK selection as `build.ps1`.
Supply `-Dotnet '<SDK>/dotnet.exe'` to select an installation. Capture and acceptance code
live under src/Testing and are absent from production builds. The test flag
alone cannot activate them: the existing runtime test configuration and exact
marker/save-directory guard must also pass. This headless check uses the
installed game's runtime; separate dedicated-executable and remote multiplayer
acceptance belongs to final feature validation.
