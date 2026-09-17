# Groupa

<p align="center">
  <img src="docs/groupa_icon.png" alt="Groupa Icon" width="128" />
</p>

<p align="center">
  <b>A Windows 11 taskbar quick launcher with jump list style popup</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-blue?logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/platform-Windows%2011-0078D4?logo=windows" alt="Windows 11" />
  <img src="https://img.shields.io/github/license/Oh-JongJin/Groupa" alt="License" />
</p>

---

## ✨ Features

- 🚀 **Quick Launch** — Pin your favorite apps to a taskbar popup for instant access
- 🎨 **Auto Theme** — Automatically follows Windows light/dark mode
- 🌍 **Multi-Language** — Auto-detects Windows display language (English / 한국어)
- 📌 **Taskbar Anchored** — Popup appears right above the taskbar icon, just like native Windows jump lists
- ⚡ **Lightweight** — Single exe (~800KB), no installation required
- 🔧 **Configurable** — Add, remove, and reorder apps through the built-in settings editor

## 📸 Screenshots

| Dark Mode | Light Mode |
|:---------:|:----------:|
| ![Dark](docs/screenshot_dark.png) | ![Light](docs/screenshot_light.png) |

## 📦 Installation

### Requirements
- Windows 10/11 (64-bit)
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

### Download
1. Download the latest release from [Releases](https://github.com/Oh-JongJin/Groupa/releases)
2. Extract to any folder
3. Run `Groupa.exe`
4. (Optional) Pin to taskbar for quick access

## 🔧 Build from Source

```bash
# Clone
git clone https://github.com/Oh-JongJin/Groupa.git
cd Groupa

# Build
dotnet publish -c Release -r win-x64

# Output
# bin/Release/net8.0-windows/win-x64/publish/Groupa.exe
```

## ⚙️ Configuration

Apps are stored in `config.json` (auto-generated on first run):

```json
{
  "group_name": "Groupa",
  "apps": [
    { "name": "", "path": "C:\\Windows\\System32\\notepad.exe" },
    { "name": "", "path": "calc.exe" },
    { "name": "", "path": "C:\\Windows\\explorer.exe" }
  ]
}
```

- **`name`**: Leave empty for auto-detection (follows OS language), or set a custom name
- **`path`**: Full path or filename (searches PATH automatically)

## 🌍 Localization

Groupa automatically detects the Windows display language:

| Windows Language | UI |
|:---:|:---:|
| 🇰🇷 한국어 | 그룹 설정, 닫기, ... |
| 🇺🇸 English (default) | Settings, Close, ... |

App names are also auto-localized (e.g., `notepad.exe` → "Notepad" / "메모장").

## 🛠️ Tech Stack

- **Framework**: .NET 8.0 / WPF
- **UI**: Windows 11 Fluent Design (Mica/Acrylic backdrop, rounded corners)
- **Icon Extraction**: Shell32 `SHGetFileInfoW`
- **Taskbar Detection**: UI Automation API
- **Theme**: DWM `DwmSetWindowAttribute` (dark/light auto-detect)

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
