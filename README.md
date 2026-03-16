# StartHandPickerMod

## Feature
- Add a `开局手牌` button in main menu.
- Configure card picks and counts before run start.
- On first `fromHandDraw` of a new run, replace default draw with your configured cards.

## Config
- Runtime config path (auto-generated):
  - `mods/StartHandPickerMod/start_hand_picker_config.json`
- Line format in UI text box:
  - `CardIdOrName,Count`
- Example:
  - `StrikeIronclad,3`
  - `DefendIronclad,2`

## Build (requires .NET 9 SDK)
1. `dotnet build modding/projects/StartHandPickerMod/StartHandPickerMod.csproj -c Release`
2. Copy output DLL to your mod folder:
   - `mods/StartHandPickerMod/StartHandPickerMod.dll`
3. Build or update your `.pck` so it contains at least:
   - `mod_manifest.json`
   - optional `mod_image.png`

## References
- `sts2.dll`
- `GodotSharp.dll`
- `0Harmony.dll`
