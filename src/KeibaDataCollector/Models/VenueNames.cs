using System.Collections.Generic;

namespace KeibaDataCollector.Models
{
    public static class VenueNames
    {
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            ["01"] = "札幌", ["02"] = "函館", ["03"] = "福島", ["04"] = "新潟", ["05"] = "東京",
            ["06"] = "中山", ["07"] = "中京", ["08"] = "京都", ["09"] = "阪神", ["10"] = "小倉",
            ["30"] = "門別", ["35"] = "盛岡", ["36"] = "水沢", ["42"] = "浦和", ["43"] = "船橋",
            ["44"] = "大井", ["45"] = "川崎", ["46"] = "金沢", ["47"] = "笠松", ["48"] = "名古屋",
            ["50"] = "園田", ["51"] = "姫路", ["54"] = "高知", ["55"] = "佐賀", ["83"] = "帯広（ばんえい）",
        };

        /// <summary>未知のコードは名前を捏造せずコードのまま返す。</summary>
        public static string Get(string trackCode) =>
            trackCode != null && Names.TryGetValue(trackCode, out var name) ? name : trackCode;
    }
}
