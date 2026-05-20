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
2. Use the `Settings` tab to set folders and options. This can be done before any PSD files exist.
3. Click `Create Folders` and `Save Settings` during first-time project setup.
4. Use the `Build UI` tab for day-to-day work.
5. Click `Extract PSDs` to generate PNG files and JSON metadata, then select JSON files and click `Build Selected UI Prefabs`.

For repeated imports, `Extract PSDs + Build` runs both steps in one click.

## JSON Format

The bundled extractor creates JSON files with `source`, `canvas`, and recursive `layers` entries. Each layer can contain `name`, `kind`, `visible`, `bbox`, `children`, and `png` fields.

If your project already has another PSD export pipeline, you can skip the extract step and point the importer at compatible JSON + PNG outputs.


## Project Settings Asset

The importer stores paths and build options in `Assets/Editor/PsdUiImporterSettings.asset`. Commit this asset to share the same PSD source folder, extraction output folder, prefab output folder, and import options with the team.

## Sprite Atlas

The `Settings` tab can optionally manage Unity Sprite Atlases. Enable `Use Sprite Atlas`, choose an atlas output folder, then click `Update Atlas`. The importer creates one atlas per selected PSD/JSON using the source name, such as `aaa.psd` -> `atlas_aaa.spriteatlas`, and registers that PSD's extracted sprite folder as the atlas packable. If `Pack atlas after build` is enabled, updated atlases are packed after prefab generation.

## Layer Tags

- `!tmp`: creates a TextMeshProUGUI object instead of an Image.
- `!btn`: adds a Button component with transition set to None.
- `!item`: saves this layer subtree as a separate item prefab. For `aa.psd` and `!item bb`, the generated prefab name is `aa_bb_Item`.
- `!mask`: adds a Mask component.
- `!cg`: adds a CanvasGroup component.
- `!kr`: Korean-only layer.
- `!en`: English-only layer.
- `!jp`: Japanese-only layer.
- `!ref`: skipped reference layer/tree.
- `!x1.5`: optional layer image scale suffix for extraction. Example: `icon !x1.5`.

Language-tagged layers are controlled by `PsdUiLanguageLayerSwitcher` on the generated prefab root.

The `Settings` tab includes a foldout tag reference for quick in-editor lookup.

## Generated Prefabs And Variants

Generated prefabs are intended to be overwritten by the importer. Keep game-specific components, wiring, animation, and overrides in prefab variants that inherit from the generated prefab.

## Project Text Localization

`!tmp` layers can be connected to project-specific text localization. Create a component that inherits `PsdUiTextLocalizerBase`, implement `TryGetText`, then assign that script in `Settings > Text Localizer Script`. During prefab generation, the importer adds the component to the generated prefab root and fills entries for every `!tmp` TextMeshProUGUI layer.

## Cleanup Images

The same editor window has a `Texture Audit` tab. Select a generated prefab and click `Refresh` to list the textures used by that prefab, plus optional textures in the matching extracted PSD folder. Column toggles can show usage paths, import settings, platform overrides, and configured atlas status. Use `Select` or `Folder` on a row to jump to the texture or its containing folder in the Project window.

## Cleanup Images

The `Cleanup Images` tab scans Texture2D assets in a selected folder and lists images that are not referenced by prefabs or scenes. Use it after regenerating PSD layer PNGs to find stale extracted images.

