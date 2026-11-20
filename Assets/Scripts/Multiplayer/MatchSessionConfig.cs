using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public enum MatchMode
{
    SingleplayerAI = 0,
    Multiplayer1v1 = 1
}

/// <summary>
/// Lưu cấu hình phiên chơi xuyên suốt giữa SampleScene (Menu/Lobby) và BattleScene.
/// Mặc định luôn là SingleplayerAI để giữ nguyên 100% luồng v1.
/// </summary>
public static class MatchSessionConfig
{
    private const string KEY_PLAYER_NAME = "YGO_MP_PlayerName";
    private const string KEY_LAST_IP = "YGO_MP_LastIP";
    private const string KEY_LAST_PORT = "YGO_MP_LastPort";
    private const string KEY_LAST_ROOM_ID = "YGO_MP_LastRoomId6";
    private const string KEY_LAST_ROOM_PWD = "YGO_MP_LastRoomPwd";

    public const string DefaultIp = "127.0.0.1";
    public const ushort DefaultPort = 7777;

    public const int MinDeckSize = 20;
    public const int MaxDeckSize = 40;
    public const int MaxCopiesPerCard = 3;

    public static MatchMode CurrentMode = MatchMode.SingleplayerAI;
    public static bool IsMultiplayer => CurrentMode == MatchMode.Multiplayer1v1;
    public static bool LocalIsHost = false;

    public static string HostPlayerName = "Host";
    public static string ClientPlayerName = "Client";

    // Bộ bài 2 người chơi đã trao đổi & xác thực ở Lobby (Bước 2)
    public static string HostDeckJson = "";
    public static string ClientDeckJson = "";
    public static int HostDeckCount = 0;
    public static int ClientDeckCount = 0;

    public static string GetLocalDisplayName()
    {
        if (!IsMultiplayer) return "Player";
        return LocalIsHost
            ? (string.IsNullOrWhiteSpace(HostPlayerName) ? "Host" : HostPlayerName)
            : (string.IsNullOrWhiteSpace(ClientPlayerName) ? "Client" : ClientPlayerName);
    }

    public static string GetOpponentDisplayName()
    {
        if (!IsMultiplayer) return "AI";
        return LocalIsHost
            ? (string.IsNullOrWhiteSpace(ClientPlayerName) ? "Đối thủ" : ClientPlayerName)
            : (string.IsNullOrWhiteSpace(HostPlayerName) ? "Host" : HostPlayerName);
    }

    public static string GetSavedPlayerName()
    {
        string saved = PlayerPrefs.GetString(KEY_PLAYER_NAME, "");
        if (string.IsNullOrWhiteSpace(saved))
        {
            saved = "Duelist";
        }
        return saved.Trim();
    }

    public static void SavePlayerName(string playerName)
    {
        string clean = string.IsNullOrWhiteSpace(playerName) ? "Duelist" : playerName.Trim();
        if (clean.Length > 20) clean = clean.Substring(0, 20);
        PlayerPrefs.SetString(KEY_PLAYER_NAME, clean);
        PlayerPrefs.Save();
    }

    public static string GenerateRandomRoomId6()
    {
        int num = UnityEngine.Random.Range(100000, 1000000);
        return num.ToString("D6");
    }

    public static bool IsValidRoomId6(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        if (s.Length != 6) return false;
        for (int i = 0; i < 6; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }

    public static ushort GetDeterministicPortFromRoomId6(string roomId6)
    {
        if (int.TryParse(roomId6, out int val) && val >= 0)
        {
            return (ushort)(12000 + (val % 40000));
        }
        return DefaultPort;
    }

    public static string GetSavedRoomId6()
    {
        string saved = PlayerPrefs.GetString(KEY_LAST_ROOM_ID, "");
        if (!IsValidRoomId6(saved))
        {
            saved = GenerateRandomRoomId6();
        }
        return saved;
    }

    public static void SaveRoomId6(string roomId6)
    {
        if (IsValidRoomId6(roomId6))
        {
            PlayerPrefs.SetString(KEY_LAST_ROOM_ID, roomId6.Trim());
            PlayerPrefs.Save();
        }
    }

    public static string GetSavedRoomPassword()
    {
        return PlayerPrefs.GetString(KEY_LAST_ROOM_PWD, "");
    }

    public static void SaveRoomPassword(string password)
    {
        string clean = string.IsNullOrEmpty(password) ? "" : password.Trim();
        if (clean.Length > 24) clean = clean.Substring(0, 24);
        PlayerPrefs.SetString(KEY_LAST_ROOM_PWD, clean);
        PlayerPrefs.Save();
    }

    public static string GetSavedIp()
    {
        string saved = PlayerPrefs.GetString(KEY_LAST_IP, DefaultIp);
        return string.IsNullOrWhiteSpace(saved) ? DefaultIp : saved.Trim();
    }

    public static void SaveIp(string ip)
    {
        string clean = string.IsNullOrWhiteSpace(ip) ? DefaultIp : ip.Trim();
        PlayerPrefs.SetString(KEY_LAST_IP, clean);
        PlayerPrefs.Save();
    }

    public static ushort GetSavedPort()
    {
        int p = PlayerPrefs.GetInt(KEY_LAST_PORT, DefaultPort);
        if (p < 1024 || p > 65535) return DefaultPort;
        return (ushort)p;
    }

    public static void SavePort(ushort port)
    {
        PlayerPrefs.SetInt(KEY_LAST_PORT, port);
        PlayerPrefs.Save();
    }

    public static void ResetToSingleplayer()
    {
        CurrentMode = MatchMode.SingleplayerAI;
        LocalIsHost = false;
        HostDeckJson = "";
        ClientDeckJson = "";
        HostDeckCount = 0;
        ClientDeckCount = 0;
    }

    /// <summary>
    /// Đọc bộ bài đã lưu của người chơi hiện tại (hoặc tự dựng Starter Deck nếu chưa từng lưu),
    /// trả về JSON dạng DeckSaveWrapper kèm trạng thái hợp lệ.
    /// </summary>
    public static string BuildLocalDeckJson(out int totalCards, out bool isValid, out string errorMsg)
    {
        List<CardSaveData> cards = DeckManager.LoadSavedDeckFromPrefs();

        if (cards == null || cards.Count == 0)
        {
            cards = BuildFallbackStarterDeckEntries();
        }

        DeckSaveWrapper wrapper = new DeckSaveWrapper { cards = cards ?? new List<CardSaveData>() };
        string json = JsonUtility.ToJson(wrapper);
        isValid = ValidateDeckJson(json, out totalCards, out errorMsg);
        return json;
    }

    public static bool ValidateDeckJson(string deckJson, out int totalCards, out string errorMsg)
    {
        totalCards = 0;
        errorMsg = "";

        if (string.IsNullOrWhiteSpace(deckJson))
        {
            errorMsg = "Dữ liệu bộ bài trống!";
            return false;
        }

        DeckSaveWrapper wrapper;
        try
        {
            wrapper = JsonUtility.FromJson<DeckSaveWrapper>(deckJson);
        }
        catch (Exception ex)
        {
            errorMsg = $"Dữ liệu bộ bài không hợp lệ ({ex.Message}).";
            return false;
        }

        if (wrapper == null || wrapper.cards == null || wrapper.cards.Count == 0)
        {
            errorMsg = "Bộ bài chưa có lá nào!";
            return false;
        }

        Dictionary<string, int> countsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in wrapper.cards)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.cardName) || item.count <= 0)
                continue;

            string cleanName = CardDatabaseSO.ExtractCleanName(item.cardName);
            if (!countsByName.ContainsKey(cleanName)) countsByName[cleanName] = 0;
            countsByName[cleanName] += item.count;

            if (countsByName[cleanName] > MaxCopiesPerCard)
            {
                errorMsg = $"Lá [{cleanName}] vượt quá tối đa {MaxCopiesPerCard} bản!";
                totalCards = countsByName.Values.Sum();
                return false;
            }

            totalCards += item.count;
        }

        if (totalCards < MinDeckSize || totalCards > MaxDeckSize)
        {
            errorMsg = $"Bộ bài hiện có {totalCards} lá (cần từ {MinDeckSize} đến {MaxDeckSize} lá)!";
            return false;
        }

        return true;
    }

    private static List<CardSaveData> BuildFallbackStarterDeckEntries()
    {
        List<string> allNames = new List<string>();
        CollectCardNamesFromFolder("Cards/Monster", allNames);
        CollectCardNamesFromFolder("Cards/Spell", allNames);
        CollectCardNamesFromFolder("Cards/Trap", allNames);

        List<CardSaveData> result = new List<CardSaveData>();
        if (allNames.Count == 0) return result;

        int total = 0;
        int copyRound = 1;
        while (total < 24 && copyRound <= MaxCopiesPerCard)
        {
            foreach (string cardName in allNames)
            {
                if (total >= 24) break;
                CardSaveData existing = result.Find(x => x.cardName.Equals(cardName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    result.Add(new CardSaveData { cardName = cardName, count = 1 });
                    total++;
                }
                else if (existing.count < MaxCopiesPerCard)
                {
                    existing.count++;
                    total++;
                }
            }
            copyRound++;
        }

        return result;
    }

    private static void CollectCardNamesFromFolder(string resourceFolder, List<string> targetList)
    {
        Sprite[] sprites = Resources.LoadAll<Sprite>(resourceFolder);
        if (sprites == null) return;
        foreach (var sp in sprites)
        {
            if (sp == null) continue;
            string clean = CardDatabaseSO.ExtractCleanName(sp.name);
            if (!string.IsNullOrEmpty(clean) && !targetList.Any(n => n.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                targetList.Add(clean);
            }
        }
    }
}
