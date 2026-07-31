# DayZ-Map.ru Companion

<p align="center">
  <img src="assets/dayz-map-companion-icon.png" alt="DayZ-Map.ru Companion" width="180">
</p>

<p align="center">
  <a href="#ru">Русский</a> · <a href="#en">English</a>
</p>

<a id="ru"></a>

## Русский

**DayZ-Map.ru Companion** — локальное Windows-приложение для обмена личными метками между DayZ-Map.ru и игрой DayZ. В приложение также входит самостоятельный модуль экранного прицела с профилями, изображениями и горячими клавишами.

Companion работает только на компьютере пользователя: API слушает `127.0.0.1`, а данные маркеров сохраняются в локальном `PrivateMarkers.json`. Приложение не требует публикации данных во внешний сервис.

### Скачать

<p>
  <a href="https://github.com/virtyzz/DayZ-Map.ru-Companion/releases/latest/download/DayZ-Map-ru-Companion-Setup-latest.exe">
    <img src="https://img.shields.io/badge/Скачать-установщик-eeb15b?style=for-the-badge&logo=windows&logoColor=111111" alt="Скачать установщик">
  </a>
</p>

Скачайте установщик из [последнего релиза](https://github.com/virtyzz/DayZ-Map.ru-Companion/releases/latest), запустите его и следуйте шагам установки. После запуска приложение находится в системном трее: двойной клик открывает окно, правый клик — меню.

К каждому релизу прикладывается полное описание изменений на русском языке: новые возможности, исправления и важные замечания по обновлению.

### Возможности Companion

- Автоматический поиск `PrivateMarkers.json` DayZ или выбор файла вручную.
- Локальный HTTP API для сайта DayZ-Map.ru:
  - `GET /api/v1/health` — состояние Companion;
  - `GET /api/v1/markers` — чтение меток из игры;
  - `POST /api/v1/import` — импорт меток в игру.
- Автоматический выбор свободного порта в диапазоне `49950–49999` или ручная настройка порта.
- Безопасная замена либо объединение меток по `uid`.
- Резервные копии `PrivateMarkers.json` перед записью и настройка их хранения.
- Наглядное состояние поиска, API, доступности файла и последней операции в разделе «Синхронизация меток».

### Модуль прицела

- Настраиваемые длина, зазор, толщина, цвет, прозрачность, точка и обводка.
- Несколько профилей для разных игр, мониторов или сценариев.
- Быстрое включение и выключение через трей или горячую клавишу.
- Выбор монитора для нескольких экранов.
- Импорт PNG/JPG как отдельного слоя прицела.

Прицел — обычное прозрачное окно поверх экрана. Он не внедряется в игры, не читает память процессов, не использует render hooks и не автоматизирует ввод.

### Горячие клавиши

- `Ctrl+Alt+X` — показать или скрыть прицел.
- `Ctrl+Alt+Left/Right` — предыдущий или следующий профиль.
- `Ctrl+Alt+Up/Down` — изменить прозрачность.
- `Ctrl+Alt+PageUp/PageDown` — изменить размер.

<a id="en"></a>

## English

**DayZ-Map.ru Companion** is a local Windows app for exchanging personal markers between DayZ-Map.ru and DayZ. It also includes an independent crosshair overlay with profiles, images, and hotkeys.

The Companion only works on the user's computer: its API listens on `127.0.0.1`, and marker data is stored in the local `PrivateMarkers.json` file. No marker data is published to an external service.

### Download

<p>
  <a href="https://github.com/virtyzz/DayZ-Map.ru-Companion/releases/latest/download/DayZ-Map-ru-Companion-Setup-latest.exe">
    <img src="https://img.shields.io/badge/Download-installer-eeb15b?style=for-the-badge&logo=windows&logoColor=111111" alt="Download installer">
  </a>
</p>

Download the installer from the [latest release](https://github.com/virtyzz/DayZ-Map.ru-Companion/releases/latest), run it, and follow the setup steps. The app then lives in the system tray: double-click opens the window and right-click opens its menu.

Each release includes detailed Russian release notes covering new features, fixes, and important update notes.

### Companion features

- Automatic discovery of DayZ's `PrivateMarkers.json`, with manual selection when needed.
- Local HTTP API for DayZ-Map.ru:
  - `GET /api/v1/health` — Companion status;
  - `GET /api/v1/markers` — read markers from the game;
  - `POST /api/v1/import` — import markers into the game.
- Automatic selection of a free port in the `49950–49999` range, or a manually configured port.
- Safe marker replacement or merge by `uid`.
- Backups of `PrivateMarkers.json` before writes, with configurable retention.

### Crosshair overlay

- Adjustable length, gap, thickness, color, opacity, dot, and outline.
- Multiple profiles for different games, monitors, or preferences.
- Quick show/hide from the tray or with a hotkey.
- Multi-monitor selection and PNG/JPG overlay import.

The overlay is a standard transparent always-on-top window. It does not inject into games, read process memory, use render hooks, or automate input.

### Hotkeys

- `Ctrl+Alt+X` — show or hide the overlay.
- `Ctrl+Alt+Left/Right` — previous or next profile.
- `Ctrl+Alt+Up/Down` — adjust opacity.
- `Ctrl+Alt+PageUp/PageDown` — adjust size.
