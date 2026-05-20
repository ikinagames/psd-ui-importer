# PSD UI Importer

Generic Unity editor package that extracts PSD/PSB files into PNG + JSON metadata and builds Unity UI prefabs from those exports.

`Tools > PSD UI Importer > Generic PSD UI Importer` is the main workflow. Project-specific generators, such as a puzzle prefab generator, should be treated as extensions built on top of this generic importer.

## Requirements

- Unity UI (uGUI)
- TextMeshPro
- Newtonsoft Json for Unity
- Python 3.x for PSD extraction
- Python packages: `psd-tools`, `pillow`

Install Python dependencies once per machine:

```bash
pip install psd-tools pillow
```

## Install As Git Package

In Unity Package Manager, choose `Install package from git URL...` and enter:

```text
https://github.com/ikinagames/psd-ui-importer.git#main
```

## Usage

1. Open `Tools > PSD UI Importer > Generic PSD UI Importer`.
2. In `PSD Extract`, set `Source PSD Folder` and `Extract Output Folder`.
3. Click `Extract PSDs` to generate PNG files and JSON metadata.
4. In `Build From Extracted JSON`, set the sprite root and output prefab folder.
5. Select JSON files and click `Build Selected UI Prefabs`.

For a first-time setup, `Extract PSDs + Build UI Prefabs` runs both steps in one click.

## JSON Format

The bundled extractor creates JSON files with `source`, `canvas`, and recursive `layers` entries. Each layer can contain `name`, `kind`, `visible`, `bbox`, `children`, and `png` fields.

If your project already has another PSD export pipeline, you can skip the extract step and point the importer at compatible JSON + PNG outputs.


## Project Settings Asset

The importer stores paths and build options in `Assets/Editor/PsdUiImporterSettings.asset`. Commit this asset to share the same PSD source folder, extraction output folder, prefab output folder, and import options with the team.

## Layer Tags

- `!tmp`: creates a TextMeshProUGUI object instead of an Image.
- `!kr`: Korean-only layer.
- `!en`: English-only layer.
- `!jp`: Japanese-only layer.
- `!ref`: skipped reference layer/tree.
- `!x1.5`: optional layer image scale suffix for extraction. Example: `icon !x1.5`.

Language-tagged layers are controlled by `PsdUiLanguageLayerSwitcher` on the generated prefab root.

## Cleanup Images

The same editor window has a `Cleanup Images` tab. It scans Texture2D assets in a selected folder and lists images that are not referenced by prefabs or scenes. Use it after regenerating PSD layer PNGs to find stale extracted images.

