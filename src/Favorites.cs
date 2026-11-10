// SPDX-License-Identifier: GPL-3.0-only
// Bakkum - Copyright (C) 2026 hirame.ki - additional terms under GPL-3.0 section 7 apply, see NOTICE.txt
// 즐겨찾기(첫 화면에 바로 나오는 고를 거리)와 칸마다 쓴 횟수.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bakkum
{
    public static class Favorites
    {
        public const int Max = 3;

        static string Key(MenuAction a, ActionOption o)
        {
            return a.Id + "/" + o.Id;
        }

        public static bool Has(MenuAction a, ActionOption o)
        {
            var list = AppSettings.Current.Favorites;
            return list != null && list.Contains(Key(a, o));
        }

        /// 넣거나 뺀다. 알릴 말이 있으면 돌려준다.
        public static string Toggle(MenuAction a, ActionOption o)
        {
            var s = AppSettings.Current;
            if (s.Favorites == null) s.Favorites = new List<string>();
            string key = Key(a, o);
            if (s.Favorites.Contains(key))
            {
                s.Favorites.Remove(key);
                s.SaveQuietly();
                return "'" + o.Label + "'을(를) 즐겨찾기에서 뺐어요.";
            }
            if (s.Favorites.Count >= Max) return "즐겨찾기는 " + Max + "개까지예요. 다른 칸을 오른쪽 단추로 눌러 먼저 빼 주세요.";
            s.Favorites.Add(key);
            s.SaveQuietly();
            return "'" + o.Label + "'을(를) 즐겨찾기에 넣었어요. 맞는 파일을 끌면 맨 앞에 나와요.";
        }

        /// 지금 놓을 파일에 쓸 수 있는 즐겨찾기
        public static List<KeyValuePair<MenuAction, ActionOption>> Applicable(IList<string> files)
        {
            var found = new List<KeyValuePair<MenuAction, ActionOption>>();
            var list = AppSettings.Current.Favorites;
            if (list == null) return found;
            foreach (var key in list)
            {
                int cut = key.IndexOf('/');
                if (cut <= 0) continue;
                string actionId = key.Substring(0, cut), optionId = key.Substring(cut + 1);
                var a = MenuAction.All.FirstOrDefault(x => x.Id == actionId);
                if (a == null || !a.CanTake(files)) continue;
                if (a.Id == "ai" && !Ai.Configured) continue;
                var o = a.OptionsFor(files).SelectMany(x => x.Flatten()).FirstOrDefault(x => x.Id == optionId && !x.HasChildren);
                if (o != null) found.Add(new KeyValuePair<MenuAction, ActionOption>(a, o));
            }
            return found;
        }

        public static void CountUse(MenuAction a)
        {
            try
            {
                var s = AppSettings.Current;
                if (s.SlotUsage == null) s.SlotUsage = new Dictionary<string, int>();
                int n;
                s.SlotUsage.TryGetValue(a.Id, out n);
                s.SlotUsage[a.Id] = n + 1;
                s.SaveQuietly();
            }
            catch { }
        }
    }
}
