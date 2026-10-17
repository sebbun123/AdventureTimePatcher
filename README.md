# AdventureTimePatcher

Installer/updater for [AdventureTime](https://github.com/sebbun123/Adventuretime) and [LazCraft](https://github.com/sebbun123/LazCraft).

Pick the product in the **Install** box (or `--product adventuretime|lazcraft` on the command line). Each installs
into its own folder under `lua/` (`adventuretime`, `Lazcraft`); an existing folder is found whatever its case.

It installs AdventureTime into a MacroQuest root:

```text
<MQ root>/lua/adventuretime/
```

## Windows usage

Download and run:

```text
AdventureTimePatcher-win-x64.exe
```

Latest Windows download: https://github.com/sebbun123/AdventureTimePatcher/releases/latest/download/AdventureTimePatcher-win-x64.exe

Pick your MacroQuest folder, then click **Check for Updates** or **Update Now**.

The patcher also checks for newer patcher releases. If one is available, use **Update Patcher** first, then continue updating AdventureTime.

## Linux usage

Latest Linux download: https://github.com/sebbun123/AdventureTimePatcher/releases/latest/download/AdventureTimePatcher-linux-x64

```bash
chmod +x AdventureTimePatcher-linux-x64
./AdventureTimePatcher-linux-x64 check --mq "/path/to/MQ/root"
./AdventureTimePatcher-linux-x64 update --mq "/path/to/MQ/root"
./AdventureTimePatcher-linux-x64 update --mq "/path/to/MQ/root" --product lazcraft
./AdventureTimePatcher-linux-x64 self-check
./AdventureTimePatcher-linux-x64 self-update
```

The MQ root is the folder that contains `lua/` and `config/`. Under Lutris/Wine, this is usually inside the game/MacroQuest install folder, not the Wine prefix root.

## What update does

- Downloads the latest `main` branch from `sebbun123/Adventuretime`.
- Installs every file listed in the repo's `manifest.txt` (`init.lua`, `modules/*.lua`, ...) plus `README.md`,
  each checked against its SHA-256 before anything is replaced. A bad download changes nothing.
- "Up to date" means the latest commit is installed **and** every manifest file is present and matches, so an
  install missing its `modules` folder is repaired on the next update.
- Never writes `Settings/`, `Logs/` or `AdventureTime_targets.ini`, and refuses any path outside the product's folder.
- LazCraft: also writes `installed.txt` (build and config stamps from the manifest), which LazCraft's Settings tab reads,
  and removes files the manifest marks `delete` (backed up first). Backups go to `config/LazCraftPatcher_backup/`.
- Preserves an existing `lua/adventuretime/AdventureTime_targets.ini`.
- If targets already exist, writes the repo copy as `AdventureTime_targets.ini.example`.
- Does not delete or replace the full `lua/adventuretime/` folder.
- Leaves extra user files and folders alone, including settings and logs.
- Backs up replaced files under:

```text
<MQ root>/config/AdventureTimePatcher_backup/<timestamp>/
```

- Writes install state to:

```text
<MQ root>/config/adventuretime_install.json
```

## Build locally

Windows GUI:

```powershell
dotnet publish src/AdventureTimePatcher.Gui/AdventureTimePatcher.Gui.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:EnableCompressionInSingleFile=true `
  -o publish/win-x64
```

Linux CLI:

```bash
dotnet publish src/AdventureTimePatcher.Cli/AdventureTimePatcher.Cli.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -o publish/linux-x64
```

The binary will be:

```text
publish/linux-x64/AdventureTimePatcher
```

Rename it to `AdventureTimePatcher-linux-x64` for release.
