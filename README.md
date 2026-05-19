# PSD UI Importer

Generic Unity editor package that builds UI prefabs from extracted PSD JSON and PNG layers.

## Requirements

- Unity UI (uGUI)
- TextMeshPro
- Newtonsoft Json for Unity

## Usage

1. Export PSD layers into JSON + PNG files. The JSON format should contain `source`, `canvas`, and recursive `layers` entries with `name`, `kind`, `visible`, `bbox`, `children`, and `png` fields.
2. Open `Tools > PSD UI Importer > Generic PSD UI Importer`.
3. Set the metadata JSON folder, sprite root folder, and output prefab folder.
4. Select JSON files and run `Build Selected UI Prefabs`.

## Layer Tags

- `!tmp`: creates a TextMeshProUGUI object instead of an Image.
- `!kr`: Korean-only layer.
- `!en`: English-only layer.
- `!jp`: Japanese-only layer.
- `!ref`: skipped reference layer/tree.

Language-tagged layers are controlled by `PsdUiLanguageLayerSwitcher` on the generated prefab root.
