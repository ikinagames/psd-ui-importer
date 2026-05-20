# Changelog

## 0.1.2

- Store Generic PSD UI Importer paths and options in a project settings asset at `Assets/Editor/PsdUiImporterSettings.asset`.
- Split the editor window into `Build UI`, `Settings`, and `Cleanup Images` tabs.
- Added optional per-PSD Sprite Atlas setup and update support.
- Added first-time setup controls that can save settings and create folders before PSD files exist.
- Added `PsdUiTextLocalizerBase` for project-specific localization of `!tmp` layers.
- Added language entry rebuilding support to `PsdUiLanguageLayerSwitcher`.
- Added a `Texture Audit` tab for prefab texture usage and import setting inspection.

## 0.1.1

- Renamed package technical name to `com.ikinagames.psd-ui-importer`.
- Updated package author to LuckyCat.
- Fixed PSD extraction argument parsing on Windows paths.

## 0.1.0

- Initial generic PSD JSON + PNG to UI prefab importer.
- Added TMP and language layer tags.
- Added Cleanup Images tab for orphaned PSD-generated textures.
- Added bundled `extract_psd.py` and editor controls for PSD/PSB extraction.



