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
    };

    private static Dictionary<string, string>? _current;

    private static Dictionary<string, string> Current
    {
        get
        {
            if (_current == null)
            {
                // Use Windows display language (changes when user switches in Settings)
                var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                _current = lang == "ko" ? Ko : En;
            }
            return _current;
        }
    }

    public static string Get(string key)
    {
        return Current.TryGetValue(key, out var value) ? value : key;
    }
}
