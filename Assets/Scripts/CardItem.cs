using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class CardItem : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler
{
    [Header("Thành phần UI trên Prefab (Tự tìm nếu để trống)")]
    public Image cardImage;
    public TextMeshProUGUI nameText;
    public TextMeshProUGUI statText;
    public TextMeshProUGUI countText;

    private GameObject countBadgeContainer;
    private CardData cardData;
    private int copyCount;
    private bool isInDeckList;
    private DeckManager manager;

    void Awake()
    {
        if (cardImage == null)
        {
            Image[] imgs = GetComponentsInChildren<Image>(true);
            foreach (var img in imgs)
            {
                if (img.gameObject != gameObject)
                {
                    cardImage = img;
                    break;
                }
            }
            if (cardImage == null) cardImage = GetComponent<Image>();
        }

        TextMeshProUGUI[] allTexts = GetComponentsInChildren<TextMeshProUGUI>(true);
        foreach (var t in allTexts)
        {
            string n = t.name.ToLower();
            if (countText == null && (n.Contains("count") || n.Contains("qty") || n.Contains("amount") || n.Contains("soluong")))
                countText = t;
            else if (nameText == null && n.Contains("name"))
                nameText = t;
            else if (statText == null && (n.Contains("stat") || n.Contains("atk") || n.Contains("lv")))
                statText = t;
        }
    }

    public void Setup(CardData data, int count, bool isInDeck, DeckManager deckManager)
    {
        cardData = data;
        copyCount = count;
        isInDeckList = isInDeck;
        manager = deckManager;

        if (cardImage != null)
        {
            cardImage.sprite = data.cardSprite;
            cardImage.preserveAspect = true;
            cardImage.color = data.cardSprite != null ? Color.white : new Color(0.25f, 0.25f, 0.35f, 1f);
        }

        if (nameText != null)
        {
            nameText.color = Color.white;
            nameText.text = data.cardName;
        }

        if (statText != null)
        {
            statText.color = Color.white;
            if (data.cardType == CardType.Monster)
                statText.text = $"LV{data.level} {data.atk}/{data.def}";
            else if (data.cardType == CardType.Trap)
                statText.text = "BẪY";
            else
                statText.text = "PHÉP";
        }

        bool showCount = isInDeck && count > 1;

        if (countText != null)
        {
            if (countBadgeContainer != null)
                countBadgeContainer.SetActive(showCount);
            else
                countText.gameObject.SetActive(showCount);

            countText.color = Color.white;
            countText.text = $"x{count}";
        }
        else if (showCount)
        {
            countBadgeContainer = new GameObject("AutoCountBadgeBox", typeof(RectTransform), typeof(Image));
            countBadgeContainer.transform.SetParent(transform, false);
            RectTransform boxRT = countBadgeContainer.GetComponent<RectTransform>();
            boxRT.anchorMin = new Vector2(1f, 1f);
            boxRT.anchorMax = new Vector2(1f, 1f);
            boxRT.pivot = new Vector2(1f, 1f);
            boxRT.anchoredPosition = new Vector2(-4f, -4f);
            boxRT.sizeDelta = new Vector2(40f, 24f);

            Image boxBg = countBadgeContainer.GetComponent<Image>();
            boxBg.color = new Color(0f, 0f, 0f, 0.78f);
            boxBg.raycastTarget = false;

            GameObject badgeObj = new GameObject("AutoCountBadge", typeof(RectTransform), typeof(TextMeshProUGUI));
            badgeObj.transform.SetParent(countBadgeContainer.transform, false);
            RectTransform rt = badgeObj.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            countText = badgeObj.GetComponent<TextMeshProUGUI>();
            countText.text = $"x{count}";
            countText.fontSize = 15;
            countText.fontStyle = FontStyles.Bold;
            countText.alignment = TextAlignmentOptions.Center;
            countText.color = Color.white;
            countText.raycastTarget = false;
        }
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (manager != null && cardData != null)
        {
            manager.ShowCardPreview(cardData);
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (manager == null || cardData == null) return;

        if (isInDeckList)
        {
            manager.RemoveCardFromDeck(cardData.cardName);
        }
        else
        {
            manager.AddCardToDeck(cardData.cardName);
        }
    }
}