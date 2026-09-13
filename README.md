# Romestead Cheat Menu

An in-game cheat menu for [Romestead](https://store.steampowered.com/app/1805320/Romestead/) (Steam, Windows).
Press **Ctrl+0** in a loaded world.

[Русская версия ниже](#на-русском)

## Features

- **Items**: spawn any item. Categories follow the [Romestead Wiki](https://romestead.wiki.gg/wiki/Items), with game icons, search and amount.
  Items drop on the ground in front of the character. Hovering an item shows the game's own tooltip.
- **Enemies & Creatures**: enemies (all except bosses), passive creatures and wild citizens (random or chosen tier).
  They appear 10 tiles in front of the character, up to 100 per click. Citizens can be talked to and invited to your settlement.
- **Terraform**: a brush for the surface map. Remove mountains and cliffs (they become flat ground), fill water and pits,
  remove forest walls, paint water, swamp, pits, cliffs, hills or any ground (grass, sand, beach, roads…).
  Paint with the mouse, undo with Ctrl+Z, pick a tile with the middle mouse button. Buildings, fields and trees are never touched.
  Changes are saved with the world.
- **Skills**: raise skill levels, add or remove Favour points.
- **Stats**: health, energy, energy regeneration, melee strength and the other stats, each with a bonus and a Freeze checkbox
  (Freeze on health means immortality).
- **Erase Cart**: a new placeable item. It looks and works like the Bronze Cart, but whatever gets loaded into it
  (logs, stones, barrels…) disappears. Players, citizens and animals are never erased.
- While the menu is open, single-player is paused and the game gets no keyboard, mouse or gamepad input.
- Menu language: English or Russian (EN / RU in the top-right corner).

## Installation

1. Download `RomesteadCheatMenu-vX.Y.Z.zip` from [Releases](https://github.com/Keha-Chester/Romestead_Mod_Menu/releases).
2. Close the game. In Steam, right-click Romestead → **Manage → Browse local files**.
3. Extract the archive into that folder, so that you get `romestead\CheatMenu\`.
4. Double-click `CheatMenu\Install.bat`.
5. Start the game, load a world and press **Ctrl+0**.

If Ctrl+0 stops working after a game update or *Verify integrity of game files*, run `Install.bat` again.

### Uninstall

Run `CheatMenu\Uninstall.bat`, then delete the `CheatMenu` folder.
Don't delete the folder first: the game would fail to start. If that has already happened,
use Steam → Romestead → **Properties → Installed Files → Verify integrity of game files**.

## Good to know

- Spawning, Favour points and terraforming work in your own world (single-player, or when you host).
  Other players see map changes after rejoining or when that area loads again.
- The pause only works in single-player.
- Terraform edits are saved into the world, so back up your save before big changes.
- Without the mod, a placed Erase Cart turns into a regular Bronze Cart, and an Erase Cart in the inventory shows as "Invalid id".
- Settings and the log are kept in `CheatMenu\` (`CheatMenuSettings.json`, `RomesteadCheatMenu.log`).
  Please attach the log when you report a problem.
- Made for Steam build 25128773 (September 2026). A game update can break parts of the mod.

## How it works

Romestead is a .NET 8 (MonoGame) game, so the mod needs no mod loader:

- `Install.bat` adds one entry to `Romestead.runtimeconfig.json`: a
  [.NET startup hook](https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md)
  with the full path to `CheatMenu\RomesteadCheatMenu.dll`. That path differs on every PC, which is why the archive
  can't simply contain a ready-made config. No game files are replaced.
- At startup the hook applies [Harmony](https://github.com/pardeike/Harmony) patches to the game's code.
- The menu is drawn with the Dear ImGui renderer that is already built into the game; icons and item tooltips come from the game itself.

| Files | Purpose |
| --- | --- |
| `StartupHook.cs`, `Plugin.cs`, `Patches.cs` | Entry point and all Harmony patches |
| `CheatMenu.cs`, `Ui.cs`, `UiFonts.cs`, `Loc.cs` | The menu window, widgets, fonts, English/Russian texts |
| `Catalog.cs`, `Spawner.cs`, `Data/` | Item and creature lists, icons, spawning |
| `Terraform.cs` | Terraform brush |
| `PlayerCheats.cs` | Skills, Favour points, stats |
| `EraseCart.cs` | Erase Cart |
| `InputBlocker.cs`, `GamePause.cs`, `GameTooltip.cs` | Input blocking, pause, game tooltips |
| `GameAccess.cs`, `EnglishLocale.cs`, `Settings.cs`, `Log.cs` | Helpers: game state, English names, settings, log |
| `package/` | Files shipped next to the mod in the archive (installer, readme) |

## Building from source

Requirements: Windows, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Romestead installed.
The project references the game's assemblies from the game folder; they are not part of this repository.

```
powershell -ExecutionPolicy Bypass -File build-release.ps1           # build and pack dist\RomesteadCheatMenu-v<version>.zip
powershell -ExecutionPolicy Bypass -File build-release.ps1 -Deploy   # the same, then install into the game
```

If the game is not in `C:\Program Files (x86)\Steam\steamapps\common\romestead`, add
`-GameDir "D:\SteamLibrary\steamapps\common\romestead"`.

## Credits

- Item categories and creature data: [Romestead Wiki](https://romestead.wiki.gg) and [romestead.tools](https://www.romestead.tools/enemies).
- [Harmony](https://github.com/pardeike/Harmony) by Andreas Pardeike (MIT License), included as `0Harmony.dll`.

This is an unofficial fan-made mod, not affiliated with the developers of Romestead. Use it at your own risk.

---

## На русском

Чит-меню для [Romestead](https://store.steampowered.com/app/1805320/Romestead/) (Steam, Windows).
Открывается в загруженном мире по **Ctrl+0**.

### Возможности

- **Items** — спавн любых предметов: категории как на [вики](https://romestead.wiki.gg/wiki/Items), иконки из игры, поиск и количество.
  Предметы падают на землю перед персонажем. При наведении показывается родная подсказка игры.
- **Enemies & Creatures** — противники (все, кроме боссов), мирные существа и дикие жители (случайный или выбранный тир).
  Появляются в 10 тайлах перед персонажем, до 100 за клик. С жителями можно поговорить и позвать их в поселение.
- **Terraform** — кисть для карты поверхности: убрать горы и скалы (станут ровной землёй), засыпать воду и ямы,
  убрать лесные стены, нарисовать воду, болото, яму, скалы, холмы или любое покрытие земли (трава, песок, пляж, дороги…).
  Рисование мышью, отмена Ctrl+Z, пипетка на средней кнопке мыши. Постройки, поля и деревья кисть не трогает.
  Изменения сохраняются вместе с миром.
- **Skills** — уровни навыков, добавление и удаление очков преимуществ (Favour points).
- **Stats** — здоровье, энергия, её восстановление, сила ближнего боя и остальные характеристики: бонус и галочка Freeze
  (Freeze на здоровье — это бессмертие).
- **Erase Cart** — новый предмет: выглядит и возит как Bronze Cart, но всё, что в неё попадает (брёвна, камни, бочки…), исчезает.
  Игроки, жители и животные не удаляются.
- Пока меню открыто, одиночная игра стоит на паузе, а клавиатура, мышь и геймпад в игру не передаются.
- Язык меню — английский или русский (EN / RU в правом верхнем углу).

### Установка

1. Скачайте `RomesteadCheatMenu-vX.Y.Z.zip` со страницы [Releases](https://github.com/Keha-Chester/Romestead_Mod_Menu/releases).
2. Закройте игру. В Steam: правый клик по Romestead → **Управление → Просмотреть локальные файлы**.
3. Распакуйте архив в эту папку, чтобы получилась папка `romestead\CheatMenu\`.
4. Запустите `CheatMenu\Install.bat` двойным кликом.
5. Запустите игру, загрузите мир и нажмите **Ctrl+0**.

Если после обновления игры или «Проверки целостности файлов» Ctrl+0 перестал работать, снова запустите `Install.bat`.

### Удаление

Запустите `CheatMenu\Uninstall.bat`, затем удалите папку `CheatMenu`.
Не удаляйте папку раньше: игра перестанет запускаться. Если это уже случилось,
в Steam: Romestead → **Свойства → Установленные файлы → Проверить целостность файлов игры**.

### Полезно знать

- Спавн, очки преимуществ и террафоминг работают в своём мире (одиночная игра или когда вы хост).
  Другие игроки увидят изменения карты после перезахода или когда этот участок загрузится заново.
- Пауза работает только в одиночной игре.
- Террафоминг сохраняется в мир, поэтому перед большими изменениями сделайте копию сохранения.
- Без мода поставленная Erase Cart превращается в обычную Bronze Cart, а Erase Cart в инвентаре показывается как «Invalid id».
- Настройки и лог лежат в `CheatMenu\` (`CheatMenuSettings.json`, `RomesteadCheatMenu.log`). Если что-то не работает, приложите лог.
- Мод сделан под Steam-сборку 25128773 (сентябрь 2026). Обновление игры может сломать часть функций.

### Как это работает

Romestead написан на .NET 8 (MonoGame), поэтому загрузчик модов не нужен. `Install.bat` добавляет в
`Romestead.runtimeconfig.json` одну запись — .NET startup hook с полным путём к `CheatMenu\RomesteadCheatMenu.dll`.
Путь у каждого свой, поэтому готовый конфиг нельзя просто положить в архив. При запуске мод патчит код игры через Harmony,
а меню рисует встроенным в игру Dear ImGui. Файлы игры не заменяются.

Сборка из исходников описана в разделе [Building from source](#building-from-source).
