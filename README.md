# Groupa

<p align="center">
  <img src="docs/groupa_icon.png" alt="Groupa Icon" width="128" />
</p>

<p align="center">
  <b>Windows 11 작업 표시줄용 퀵 런처</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-blue?logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/platform-Windows%2011-0078D4?logo=windows" alt="Windows 11" />
  <img src="https://img.shields.io/github/license/Oh-JongJin/Groupa" alt="License" />
</p>

---

작업 표시줄에 고정해두고, 클릭하면 자주 쓰는 앱 목록이 점프 리스트처럼 팝업으로 뜹니다.

## Features

- 작업 표시줄 아이콘 바로 위에 팝업이 표시됨
- Windows 다크/라이트 모드 자동 감지
- OS 표시 언어에 따라 한국어/영어 자동 전환
- 단일 exe (~1MB), 별도 설치 불필요
- GUI 설정 에디터로 앱 추가/삭제/정렬 가능

## Screenshots

| Dark Mode | Light Mode |
|:---------:|:----------:|
| ![Dark](docs/screenshot_dark.png) | ![Light](docs/screenshot_light.png) |

## Installation

**Requirements**
- Windows 10/11 (64-bit)
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

**Download**
1. [Releases](https://github.com/Oh-JongJin/Groupa/releases) 페이지에서 최신 버전 다운로드
2. 원하는 폴더에 압축 해제
3. `Groupa.exe` 실행
4. 작업 표시줄에 고정하면 편하게 사용 가능

## Build from Source

```bash
git clone https://github.com/Oh-JongJin/Groupa.git
cd Groupa
dotnet publish -c Release -r win-x64
# bin/Release/net8.0-windows/win-x64/publish/Groupa.exe
```

## Configuration

앱 목록은 `config.json`에 저장됩니다 (최초 실행 시 자동 생성):

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

- `name` — 비워두면 OS 언어에 맞춰 자동 감지 (예: `notepad.exe` → "메모장" / "Notepad")
- `path` — 전체 경로 또는 파일명 (PATH 자동 검색)

팝업 하단의 **그룹 설정** 버튼을 누르면 GUI로도 편집할 수 있습니다.

## Tech Stack

| | |
|---|---|
| Framework | .NET 8.0 / WPF |
| UI | Windows 11 Fluent Design (Mica/Acrylic, rounded corners) |
| Icon Extraction | Shell32 `SHGetFileInfoW` |
| Taskbar Detection | UI Automation API |
| Theme | DWM `DwmSetWindowAttribute` |

## License

MIT License — [LICENSE](LICENSE) 참고.
