using System.Collections.Generic;
using System.Globalization;

namespace JumpListLauncher.Helpers;

public static class Strings
{
    private static readonly Dictionary<string, string> Ko = new()
    {
        ["Settings"]       = "⚙️ 그룹 설정",
        ["Close"]          = "❌ 닫기",
        ["EditorTitle"]    = "그룹 설정",
        ["GroupName"]      = "그룹 이름",
        ["AppList"]        = "등록된 앱 목록",
        ["AddApp"]         = "➕ 앱 추가",
        ["Remove"]         = "➖ 삭제",
        ["MoveUp"]         = "🔼 위로",
        ["MoveDown"]       = "🔽 아래로",
        ["Save"]           = "저장",
        ["Cancel"]         = "취소",
        ["ErrorLoad"]      = "설정 불러오기 실패",
        ["ErrorLaunch"]    = "실행 실패",
        ["Error"]          = "오류",
        ["Preferences"]    = "환경 설정",
        ["ThemeLabel"]     = "테마",
        ["ThemeSystem"]    = "시스템 설정",
        ["ThemeDark"]      = "다크",
        ["ThemeLight"]     = "라이트",
        ["LanguageLabel"]  = "언어",
        ["LangSystem"]     = "시스템 설정",
        ["LangKorean"]     = "한국어",
        ["LangEnglish"]    = "English",
        ["RestartNotice"]  = "변경 사항은 다음 실행 시 적용됩니다.",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["Settings"]       = "⚙️ Settings",
        ["Close"]          = "❌ Close",
        ["EditorTitle"]    = "Group Settings",
        ["GroupName"]      = "Group Name",
        ["AppList"]        = "Registered Apps",
        ["AddApp"]         = "➕ Add App",
        ["Remove"]         = "➖ Remove",
        ["MoveUp"]         = "🔼 Up",
        ["MoveDown"]       = "🔽 Down",
        ["Save"]           = "Save",
        ["Cancel"]         = "Cancel",
        ["ErrorLoad"]      = "Failed to load config",
        ["ErrorLaunch"]    = "Failed to launch",
        ["Error"]          = "Error",
        ["Preferences"]    = "Preferences",
        ["ThemeLabel"]     = "Theme",
        ["ThemeSystem"]    = "System",
        ["ThemeDark"]      = "Dark",
        ["ThemeLight"]     = "Light",
        ["LanguageLabel"]  = "Language",
        ["LangSystem"]     = "System",
        ["LangKorean"]     = "한국어",
        ["LangEnglish"]    = "English",
        ["RestartNotice"]  = "Changes will be applied on next launch.",
    };

    private static Dictionary<string, string>? _current;
    private static string? _forcedLang;

    /// <summary>
    /// Set forced language from preferences. Call before any Get().
    /// </summary>
    public static void SetLanguage(string lang)
    {
        _forcedLang = lang;
        _current = null; // reset cache
    }

    private static Dictionary<string, string> Current
    {
        get
        {
            if (_current == null)
            {
                string lang;
                if (!string.IsNullOrEmpty(_forcedLang) && _forcedLang != "system")
                    lang = _forcedLang;
                else
                    lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

                _current = lang == "ko" ? Ko : En;
            }
            return _current;
        }
    }

    public static string Get(string key)
    {
        return Current.TryGetValue(key, out var value) ? value : key;
    }

    public static bool IsKorean => Current == Ko;
}
