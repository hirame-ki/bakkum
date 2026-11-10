// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 설정을 %APPDATA%\Bakkum\settings.json 에 둔다. API 키는 윈도우 사용자 암호화(DPAPI)로 감싸 저장한다.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Bakkum
{
    public class AppSettings
    {
        /// index(색인, 기본) · fan(부채) · wave(물결) · honeycomb(벌집)
        public string MenuShape { get; set; }
        /// Shift · Ctrl · Q · Off
        public string TriggerKey { get; set; }
        /// 탐색기가 아닌 프로그램에서 끌 때도 메뉴를 띄울지
        public bool AnySource { get; set; }
        public bool StartWithWindows { get; set; }
        public bool RevealResult { get; set; }
        /// none · claude-code · chatgpt · gemini-key · openrouter
        public string AiProvider { get; set; }
        public string GeminiKeyProtected { get; set; }
        public string ClaudePath { get; set; }
        /// Claude Code가 구독이 아닌 '쓴 만큼 요금' 방식으로 로그인돼 있어도 쓸지(선생님이 직접 켜야 함)
        public bool ClaudeAllowMetered { get; set; }
        public string OpenRouterKeyProtected { get; set; }
        /// 지난번에 잘 읽은 OpenRouter 무료 모델(다음에 먼저 써 봄)
        public string OpenRouterLastModel { get; set; }
        /// OpenRouter에서 보낸 내용을 기록·학습하지 않는 곳으로만 보낼지(무료 모델이 줄어듦)
        public bool OpenRouterNoTraining { get; set; }
        /// 메뉴 칸 순서(할 일 이름). 없는 칸은 기본 순서로 뒤에 붙는다.
        public System.Collections.Generic.List<string> SlotOrder { get; set; }
        /// 메뉴에서 숨긴 칸
        public System.Collections.Generic.List<string> HiddenSlots { get; set; }
        /// 칸마다 쓴 횟수(자주 쓰는 칸 앞으로)
        public System.Collections.Generic.Dictionary<string, int> SlotUsage { get; set; }
        /// 자주 쓰는 칸을 저절로 앞으로
        public bool AutoOrder { get; set; }
        /// 즐겨찾기: "칸/고를거리" 최대 3개. 첫 화면에 바로 나온다.
        public System.Collections.Generic.List<string> Favorites { get; set; }
        /// 처음 쓸 때 사용법을 보여 줬는지
        public bool GuideShown { get; set; }
        /// 새 버전 알림 받기
        public bool CheckUpdates { get; set; }
        /// 마지막으로 새 버전을 확인한 때(yyyy-MM-dd)
        public string LastUpdateCheck { get; set; }
        /// 앱 목록 등록을 시도한 버전(버전마다 한 번만 시도한다)
        public string AppListTried { get; set; }
        /// 지난번에 고른 화면 녹화 영역: full · area
        public string RecordArea { get; set; }
        /// 지난번에 고른 화면 녹화 소리: none · mic · computer · both
        public string RecordSound { get; set; }

        public AppSettings()
        {
            SlotOrder = new System.Collections.Generic.List<string>();
            HiddenSlots = new System.Collections.Generic.List<string>();
            SlotUsage = new System.Collections.Generic.Dictionary<string, int>();
            AutoOrder = false;
            Favorites = new System.Collections.Generic.List<string>();
            GuideShown = false;
            CheckUpdates = true;
            LastUpdateCheck = "";
            AppListTried = "";
            RecordArea = "full";
            RecordSound = "none";
            ClaudeAllowMetered = false;
            MenuShape = "index";
            TriggerKey = "Shift";
            AnySource = false;
            StartWithWindows = true;
            RevealResult = false;
            AiProvider = "none";
            GeminiKeyProtected = "";
            ClaudePath = "";
            OpenRouterKeyProtected = "";
            OpenRouterLastModel = "";
            OpenRouterNoTraining = false;
        }

        [ScriptIgnore]
        public string GeminiKey
        {
            get { return Unprotect(GeminiKeyProtected); }
            set { GeminiKeyProtected = string.IsNullOrEmpty(value) ? "" : Protect(value.Trim()); }
        }

        [ScriptIgnore]
        public string OpenRouterKey
        {
            get { return Unprotect(OpenRouterKeyProtected); }
            set { OpenRouterKeyProtected = string.IsNullOrEmpty(value) ? "" : Protect(value.Trim()); }
        }

        static string FilePath
        {
            get { return Path.Combine(AppInfo.DataDir, "settings.json"); }
        }

        static AppSettings current;

        public static AppSettings Current
        {
            get
            {
                if (current == null) current = Load();
                return current;
            }
        }

        public static event Action Changed;

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                Log.Write("설정 읽기 실패: " + ex.GetType().Name);
            }
            return new AppSettings();
        }

        public void Save()
        {
            SaveQuietly();
            var h = Changed;
            if (h != null) h();
        }

        /// 알림 없이 파일에만 쓴다(작업 중 다른 스레드에서 작은 값을 기억할 때).
        public void SaveQuietly()
        {
            lock (SaveGate)
            {
                try
                {
                    string json = new JavaScriptSerializer().Serialize(this);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                }
                catch (Exception ex)
                {
                    Log.Write("설정 저장 실패: " + ex.GetType().Name);
                }
            }
        }

        static readonly object SaveGate = new object();

        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("bakkum-v1");

        public static string Protect(string plain)
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return "";
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }
    }
}
