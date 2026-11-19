using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using TMPro;

[Serializable]
public class CardSaveData
{
    public string cardName;
    public int count;
}

[Serializable]
public class DeckSaveWrapper
{
    public List<CardSaveData> cards = new List<CardSaveData>();
}

public class DeckManager : MonoBehaviour
{
    public static DeckManager Instance;

    public const string DECK_SAVE_KEY = "PlayerEquippedDeck_V1";

    [Header("Cấu hình Bộ Bài")]
    public int minDeckSize = 20;
    public int maxDeckSize = 40;
    public int maxCopiesPerCard = 3;
    public CardDatabaseSO cardDatabase;
    public string monsterResourceFolder = "Cards/Monster";
    public string spellResourceFolder = "Cards/Spell";
    public string trapResourceFolder = "Cards/Trap";
    public string battleSceneName = "BattleScene";

    [Header("UI Danh Sách Bài (Kéo thả hoặc tự nhận diện)")]
    public Transform collectionContent;
    public Transform deckContent;
    public GameObject cardItemPrefab;

    [Header("UI Thông Tin & Nút Bấm")]
    public TextMeshProUGUI deckCountText;
    public TextMeshProUGUI statusText;
    public Button playButton;

    [Header("UI Xem Chi Tiết Lá Bài (Tùy chọn)")]
    public Image previewImage;
    public TextMeshProUGUI previewNameText;
    public TextMeshProUGUI previewStatsText;
    public TextMeshProUGUI previewDescText;

    private List<CardData> allAvailableCards = new List<CardData>();
    private List<CardSaveData> equippedDeck = new List<CardSaveData>();
    private GameObject runtimeCardTemplate;

    void Awake()
    {
        Instance = this;
        Time.timeScale = 1f;

        if (FindObjectOfType<EventSystem>() == null)
        {
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        if (cardDatabase == null)
        {
            cardDatabase = Resources.Load<CardDatabaseSO>("CardDatabase");
        }
    }

    void Start()
    {
        AutoDiscoverUI();
        LoadAllCardData();
        LoadDeckFromPrefs();
        RefreshAllUI();
    }

    void AutoDiscoverUI()
    {
        // 1. Tự nhận diện 2 khung chứa bài (Kho bài phía trên & Bộ bài phía dưới) nếu Inspector bị mất liên kết
        if (collectionContent == null || deckContent == null)
        {
            ScrollRect[] scrolls = FindObjectsOfType<ScrollRect>(true)
                .Where(s => s.content != null && s.GetComponentInParent<Canvas>() != null)
                .OrderByDescending(s => s.GetComponent<RectTransform>().position.y)
                .ToArray();

            if (scrolls.Length >= 2)
            {
                if (collectionContent == null) collectionContent = scrolls[0].content;
                if (deckContent == null) deckContent = scrolls[1].content;
            }
            else if (scrolls.Length == 1)
            {
                if (deckContent == null) deckContent = scrolls[0].content;

                if (collectionContent == null)
                {
                    RectTransform scrollRT = scrolls[0].GetComponent<RectTransform>();
                    RectTransform topPanel = FindObjectsOfType<Image>(true)
                        .Select(img => img.GetComponent<RectTransform>())
                        .Where(rt => rt != null &&
                                     rt != scrollRT &&
                                     !rt.IsChildOf(scrollRT) &&
                                     rt.GetComponentInParent<Canvas>() != null &&
                                     rt.rect.width > 400f &&
                                     rt.rect.height > 150f &&
                                     rt.position.y > scrollRT.position.y)
                        .OrderByDescending(rt => rt.rect.width * rt.rect.height)
                        .FirstOrDefault();

                    if (topPanel != null) collectionContent = topPanel;
                }
            }
            else
            {
                RectTransform[] panels = FindObjectsOfType<Image>(true)
                    .Select(img => img.GetComponent<RectTransform>())
                    .Where(rt => rt != null &&
                                 rt.GetComponentInParent<Canvas>() != null &&
                                 rt.rect.width > 400f &&
                                 rt.rect.height > 100f)
                    .OrderByDescending(rt => rt.position.y)
                    .ToArray();

                if (panels.Length >= 2)
                {
                    if (collectionContent == null) collectionContent = panels[0];
                    if (deckContent == null) deckContent = panels[1];
                }
            }
        }

        EnsureGridLayout(collectionContent, new Vector2(110f, 158f));
        EnsureGridLayout(deckContent, new Vector2(92f, 134f));

        // 2. Tự tạo mẫu CardItem nếu cardItemPrefab bị mất liên kết (chữ trắng + khung chữ nhật đen bán trong suốt)
        if (cardItemPrefab == null)
        {
            runtimeCardTemplate = new GameObject("RuntimeCardItemTemplate", typeof(RectTransform), typeof(Image), typeof(CardItem));
            runtimeCardTemplate.SetActive(false);

            GameObject statBox = new GameObject("StatBox", typeof(RectTransform), typeof(Image));
            statBox.transform.SetParent(runtimeCardTemplate.transform, false);
            RectTransform boxRT = statBox.GetComponent<RectTransform>();
            boxRT.anchorMin = new Vector2(0.5f, 0f);
            boxRT.anchorMax = new Vector2(0.5f, 0f);
            boxRT.pivot = new Vector2(0.5f, 0f);
            boxRT.anchoredPosition = new Vector2(0f, 2f);
            boxRT.sizeDelta = new Vector2(106f, 22f);
            Image boxImg = statBox.GetComponent<Image>();
            boxImg.color = new Color(0f, 0f, 0f, 0.76f);
            boxImg.raycastTarget = false;

            GameObject statObj = new GameObject("StatText", typeof(RectTransform), typeof(TextMeshProUGUI));
            statObj.transform.SetParent(statBox.transform, false);
            RectTransform sRT = statObj.GetComponent<RectTransform>();
            sRT.anchorMin = Vector2.zero;
            sRT.anchorMax = Vector2.one;
            sRT.offsetMin = new Vector2(2f, 0f);
            sRT.offsetMax = new Vector2(-2f, 0f);

            TextMeshProUGUI sTxt = statObj.GetComponent<TextMeshProUGUI>();
            sTxt.fontSize = 12;
            sTxt.fontStyle = FontStyles.Bold;
            sTxt.alignment = TextAlignmentOptions.Center;
            sTxt.color = Color.white;
            sTxt.raycastTarget = false;

            cardItemPrefab = runtimeCardTemplate;
        }

        // 3. Tự tạo nhãn đếm số lá bài (20 - 40 lá) với chữ trắng + khung chữ nhật đen bán trong suốt
        if (deckCountText == null && deckContent != null)
        {
            Canvas canvas = deckContent.GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                GameObject cntBox = new GameObject("AutoDeckCountBox", typeof(RectTransform), typeof(Image));
                cntBox.transform.SetParent(deckContent.parent != null ? deckContent.parent : canvas.transform, false);
                RectTransform boxRT = cntBox.GetComponent<RectTransform>();
                boxRT.anchorMin = new Vector2(0.5f, 1f);
                boxRT.anchorMax = new Vector2(0.5f, 1f);
                boxRT.pivot = new Vector2(0.5f, 1f);
                boxRT.anchoredPosition = new Vector2(0f, -4f);
                boxRT.sizeDelta = new Vector2(440f, 30f);
                Image boxImg = cntBox.GetComponent<Image>();
                boxImg.color = new Color(0f, 0f, 0f, 0.76f);
                boxImg.raycastTarget = false;

                GameObject cntObj = new GameObject("AutoDeckCountText", typeof(RectTransform), typeof(TextMeshProUGUI));
                cntObj.transform.SetParent(cntBox.transform, false);
                RectTransform rt = cntObj.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(6f, 0f);
                rt.offsetMax = new Vector2(-6f, 0f);

                deckCountText = cntObj.GetComponent<TextMeshProUGUI>();
                deckCountText.fontSize = 17;
                deckCountText.fontStyle = FontStyles.Bold;
                deckCountText.alignment = TextAlignmentOptions.Center;
                deckCountText.color = Color.white;
                deckCountText.raycastTarget = false;
            }
        }
        else if (deckCountText != null)
        {
            deckCountText.color = Color.white;
        }

        if (statusText != null)
        {
            statusText.color = Color.white;
        }

        // 4. Tự tìm nút Chơi / Đấu trên Menu chính
        if (playButton == null)
        {
            Button[] allButtons = FindObjectsOfType<Button>(true);
            foreach (var btn in allButtons)
            {
                string n = btn.name.ToLower();
                TextMeshProUGUI tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
                string label = tmp != null ? tmp.text.ToLower() : "";

                if (n.Contains("play") || n.Contains("start") || n.Contains("duel") || n.Contains("battle") ||
                    label.Contains("chơi") || label.Contains("play") || label.Contains("đấu") || label.Contains("chiến"))
                {
                    playButton = btn;
                    break;
                }
            }
        }

        if (playButton != null && playButton.onClick.GetPersistentEventCount() == 0)
        {
            playButton.onClick.RemoveAllListeners();
            playButton.onClick.AddListener(StartBattle);
        }
    }

    void EnsureGridLayout(Transform container, Vector2 cellSize)
    {
        if (container == null) return;
        if (container.GetComponent<LayoutGroup>() == null)
        {
            GridLayoutGroup grid = container.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = cellSize;
            grid.spacing = new Vector2(12f, 12f);
            grid.padding = new RectOffset(14, 14, 28, 12);
            grid.childAlignment = TextAnchor.UpperLeft;
        }
        if (container.GetComponentInParent<ScrollRect>() != null && container.GetComponent<ContentSizeFitter>() == null)
        {
            ContentSizeFitter fitter = container.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
    }

    void LoadAllCardData()
    {
        allAvailableCards.Clear();

        // 1. Nạp Quái Thú (Resources/Cards/Monster)
        Sprite[] monsterSprites = Resources.LoadAll<Sprite>(monsterResourceFolder);
        foreach (var sp in monsterSprites)
        {
            if (sp == null) continue;
            CardData data = cardDatabase != null
                ? cardDatabase.BuildCardData(sp.name, sp, false)
                : CardDatabaseSO.AutoBuildFallbackMonster(sp.name, sp);
            allAvailableCards.Add(data);
        }

        // 2. Nạp Bài Phép (Resources/Cards/Spell)
        if (!string.IsNullOrEmpty(spellResourceFolder))
        {
            Sprite[] spellSprites = Resources.LoadAll<Sprite>(spellResourceFolder);
            foreach (var sp in spellSprites)
            {
                if (sp == null) continue;
                CardData data = cardDatabase != null
                    ? cardDatabase.BuildCardData(sp.name, sp, true)
                    : CardDatabaseSO.AutoBuildFallbackSpell(sp.name, sp);
                allAvailableCards.Add(data);
            }
        }

        // 3. Nạp Bài Bẫy (Resources/Cards/Trap)
        if (!string.IsNullOrEmpty(trapResourceFolder))
        {
            Sprite[] trapSprites = Resources.LoadAll<Sprite>(trapResourceFolder);
            foreach (var sp in trapSprites)
            {
                if (sp == null) continue;
                CardData data = cardDatabase != null
                    ? cardDatabase.BuildCardData(sp.name, sp, true)
                    : CardDatabaseSO.AutoBuildFallbackSpell(sp.name, sp);
                data.cardType = CardType.Trap;
                allAvailableCards.Add(data);
            }
        }
    }

    public void LoadDeckFromPrefs()
    {
        equippedDeck.Clear();

        List<CardSaveData> rawSaved = LoadSavedDeckFromPrefs();
        foreach (var saved in rawSaved)
        {
            CardData matched = FindCardByName(saved.cardName);
            if (matched != null)
            {
                equippedDeck.Add(new CardSaveData
                {
                    cardName = matched.cardName,
                    count = Mathf.Clamp(saved.count, 1, maxCopiesPerCard)
                });
            }
        }

        if (GetTotalDeckCardCount() == 0 && allAvailableCards.Count > 0)
        {
            AutoBuildStarterDeck();
        }
    }

    public static List<CardSaveData> LoadSavedDeckFromPrefs()
    {
        List<CardSaveData> result = new List<CardSaveData>();
        if (!PlayerPrefs.HasKey(DECK_SAVE_KEY)) return result;

        string json = PlayerPrefs.GetString(DECK_SAVE_KEY, "");
        if (string.IsNullOrEmpty(json)) return result;

        DeckSaveWrapper wrapper = JsonUtility.FromJson<DeckSaveWrapper>(json);
        if (wrapper != null && wrapper.cards != null)
        {
            foreach (var item in wrapper.cards)
            {
                if (item != null && !string.IsNullOrEmpty(item.cardName) && item.count > 0)
                {
                    result.Add(new CardSaveData { cardName = item.cardName, count = item.count });
                }
            }
        }
        return result;
    }

    public void SaveDeckToPrefs()
    {
        DeckSaveWrapper wrapper = new DeckSaveWrapper { cards = equippedDeck };
        string json = JsonUtility.ToJson(wrapper);
        PlayerPrefs.SetString(DECK_SAVE_KEY, json);
        PlayerPrefs.Save();
    }

    public void AutoBuildStarterDeck()
    {
        equippedDeck.Clear();
        if (allAvailableCards.Count == 0) return;

        int total = 0;
        int copyRound = 1;
        while (total < 24 && copyRound <= maxCopiesPerCard)
        {
            foreach (var card in allAvailableCards)
            {
                if (total >= 24) break;
                CardSaveData existing = equippedDeck.Find(x => x.cardName.Equals(card.cardName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    equippedDeck.Add(new CardSaveData { cardName = card.cardName, count = 1 });
                    total++;
                }
                else if (existing.count < maxCopiesPerCard)
                {
                    existing.count++;
                    total++;
                }
            }
            copyRound++;
        }

        SaveDeckToPrefs();
    }

    public CardData FindCardByName(string rawOrCleanName)
    {
        if (string.IsNullOrEmpty(rawOrCleanName)) return null;
        string clean = CardDatabaseSO.ExtractCleanName(rawOrCleanName);

        return allAvailableCards.Find(c =>
            c.cardName.Equals(rawOrCleanName, StringComparison.OrdinalIgnoreCase) ||
            c.cardName.Equals(clean, StringComparison.OrdinalIgnoreCase));
    }

    public int GetTotalDeckCardCount()
    {
        return equippedDeck.Sum(c => c.count);
    }

    public List<CardSaveData> GetEquippedDeckData()
    {
        return equippedDeck.Select(c => new CardSaveData { cardName = c.cardName, count = c.count }).ToList();
    }

    public void AddCardToDeck(string cardName)
    {
        CardData card = FindCardByName(cardName);
        if (card == null) return;

        ShowCardPreview(card);

        int currentTotal = GetTotalDeckCardCount();
        if (currentTotal >= maxDeckSize)
        {
            SetStatus($"Bộ bài đã đạt tối đa {maxDeckSize} lá!");
            return;
        }

        CardSaveData existing = equippedDeck.Find(c => c.cardName.Equals(card.cardName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (existing.count >= maxCopiesPerCard)
            {
                SetStatus($"Mỗi lá bài chỉ được tối đa {maxCopiesPerCard} bản (x{maxCopiesPerCard})!");
                return;
            }
            existing.count++;
        }
        else
        {
            equippedDeck.Add(new CardSaveData { cardName = card.cardName, count = 1 });
        }

        SaveDeckToPrefs();
        RefreshDeckUI();
    }

    public void RemoveCardFromDeck(string cardName)
    {
        CardData card = FindCardByName(cardName);
        if (card != null) ShowCardPreview(card);

        CardSaveData existing = equippedDeck.Find(c => c.cardName.Equals(cardName, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;

        existing.count--;
        if (existing.count <= 0)
        {
            equippedDeck.Remove(existing);
        }

        SaveDeckToPrefs();
        RefreshDeckUI();
    }

    public void RefreshAllUI()
    {
        RefreshCollectionUI();
        RefreshDeckUI();
        if (allAvailableCards.Count > 0)
        {
            ShowCardPreview(allAvailableCards[0]);
        }
    }

    public void RefreshCollectionUI()
    {
        if (collectionContent == null || cardItemPrefab == null) return;

        for (int i = collectionContent.childCount - 1; i >= 0; i--)
        {
            Destroy(collectionContent.GetChild(i).gameObject);
        }

        foreach (var card in allAvailableCards)
        {
            GameObject itemObj = Instantiate(cardItemPrefab, collectionContent);
            itemObj.SetActive(true);
            CardItem itemUI = itemObj.GetComponent<CardItem>();
            if (itemUI != null)
            {
                itemUI.Setup(card, 1, isInDeck: false, this);
            }
        }
    }

    public void RefreshDeckUI()
    {
        int total = GetTotalDeckCardCount();
        if (deckCountText != null)
        {
            deckCountText.color = Color.white;
            deckCountText.text = $"Bộ bài: {total} / {maxDeckSize} (Tối thiểu {minDeckSize})";
        }

        if (deckContent == null || cardItemPrefab == null) return;

        for (int i = deckContent.childCount - 1; i >= 0; i--)
        {
            Destroy(deckContent.GetChild(i).gameObject);
        }

        foreach (var entry in equippedDeck)
        {
            CardData card = FindCardByName(entry.cardName);
            if (card == null) continue;

            GameObject itemObj = Instantiate(cardItemPrefab, deckContent);
            itemObj.SetActive(true);
            CardItem itemUI = itemObj.GetComponent<CardItem>();
            if (itemUI != null)
            {
                itemUI.Setup(card, entry.count, isInDeck: true, this);
            }
        }
    }

    public void ShowCardPreview(CardData card)
    {
        if (card == null) return;

        if (previewImage != null)
        {
            previewImage.sprite = card.cardSprite;
            previewImage.preserveAspect = true;
            previewImage.color = card.cardSprite != null ? Color.white : Color.gray;
        }
        if (previewNameText != null)
        {
            previewNameText.color = Color.white;
            previewNameText.text = card.cardName;
        }
        if (previewStatsText != null)
        {
            previewStatsText.text = card.cardType == CardType.Monster
                ? $"[{card.CardTag}] LV{card.level} | {card.atk}/{card.def}"
                : $"[{card.CardTag}]";
        }
        if (previewDescText != null) previewDescText.text = card.description;
    }

    void SetStatus(string msg)
    {
        if (statusText != null)
        {
            statusText.color = Color.white;
            statusText.text = msg;
        }
        Debug.Log($"[DeckManager] {msg}");
    }

    public void StartBattle()
    {
        int total = GetTotalDeckCardCount();
        if (total > 0 && (total < minDeckSize || total > maxDeckSize))
        {
            SetStatus($"Bộ bài cần từ {minDeckSize} đến {maxDeckSize} lá để vào trận (hiện có {total} lá)!");
            return;
        }

        SaveDeckToPrefs();
        Time.timeScale = 1f;
        SceneManager.LoadScene(battleSceneName);
    }

    public void PlayGame() => StartBattle();
    public void StartGame() => StartBattle();
    public void StartDuel() => StartBattle();
    public void OnPlayButtonClicked() => StartBattle();
}