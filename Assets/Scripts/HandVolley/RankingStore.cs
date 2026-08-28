using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HandVolley
{
    /// <summary>
    /// 턴 결과를 시간대별로 저장하고 순위를 매긴다.
    /// 서버가 없는 로컬 게임이라 PlayerPrefs 에 JSON 으로 저장한다.
    /// JsonUtility 는 최상위 배열/리스트를 직렬화하지 못하므로 래퍼 클래스(RankingData)로 감싼다.
    /// </summary>
    public class RankingStore : MonoBehaviour
    {
        private const string PrefsKey = "HandVolley_Rankings";
        private const int MaxEntries = 200;

        [Serializable]
        public class RankingEntry
        {
            public int score;
            public float bestDistance;
            public string timestampIso;
        }

        [Serializable]
        private class RankingData
        {
            public List<RankingEntry> entries = new List<RankingEntry>();
        }

        // 시간대 구분 (로컬 DateTime.Hour 기준). 필요하면 여기만 바꾸면 된다.
        //   아침 05-10시, 점심 11-13시, 오후 14-17시, 저녁 18-04시(자정 넘김 포함)
        private static readonly (string name, int startHour, int endHour)[] Buckets =
        {
            ("아침", 5, 10),
            ("점심", 11, 13),
            ("오후", 14, 17),
            ("저녁", 18, 4),   // 18시부터 다음날 04시까지 (자정을 넘어간다)
        };

        private RankingData _data;

        private void Awake()
        {
            Load();
        }

        public static string GetBucketName(DateTime time)
        {
            int hour = time.Hour;
            foreach (var b in Buckets)
            {
                bool inRange = b.startHour <= b.endHour
                    ? hour >= b.startHour && hour <= b.endHour
                    : hour >= b.startHour || hour <= b.endHour;   // 자정을 넘기는 구간
                if (inRange) return b.name;
            }
            return Buckets[0].name;
        }

        public string GetCurrentBucketName() => GetBucketName(DateTime.Now);

        public void AddEntry(int score, float bestDistance)
        {
            _data.entries.Add(new RankingEntry
            {
                score = score,
                bestDistance = bestDistance,
                timestampIso = DateTime.Now.ToString("o"),
            });

            if (_data.entries.Count > MaxEntries)
            {
                // 오래된 것부터 정리 — 시간순으로 저장되므로 앞에서부터 자른다.
                _data.entries.RemoveRange(0, _data.entries.Count - MaxEntries);
            }

            Save();
        }

        /// <summary>지정한 시간대의 상위 기록을 점수순으로 반환.</summary>
        public List<RankingEntry> GetTop(string bucketName, int count)
        {
            return _data.entries
                .Where(e => DateTime.TryParse(e.timestampIso, out var t) && GetBucketName(t) == bucketName)
                .OrderByDescending(e => e.score)
                .Take(count)
                .ToList();
        }

        /// <summary>score 가 지정 시간대 안에서 몇 위인지 (1부터 시작). 기록이 없으면 1.</summary>
        public int GetRank(string bucketName, int score)
        {
            var scores = _data.entries
                .Where(e => DateTime.TryParse(e.timestampIso, out var t) && GetBucketName(t) == bucketName)
                .Select(e => e.score)
                .ToList();
            return scores.Count(s => s > score) + 1;
        }

        private void Load()
        {
            string json = PlayerPrefs.GetString(PrefsKey, "");
            _data = string.IsNullOrEmpty(json) ? new RankingData() : JsonUtility.FromJson<RankingData>(json);
            if (_data == null) _data = new RankingData();
        }

        private void Save()
        {
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(_data));
        }
    }
}
