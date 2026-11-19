using UnityEngine;
using UnityEngine.EventSystems;

public class BattleCardDragUI : MonoBehaviour, IPointerEnterHandler, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    private int handIndex;
    private CardData cardData;
    private RectTransform rectTransform;
    private CanvasGroup canvasGroup;
    private Transform originalParent;
    private int originalSiblingIndex;
    private Vector2 originalAnchoredPos;

    void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        canvasGroup = GetComponent<CanvasGroup>();
    }

    public void Setup(int index, CardData data)
    {
        handIndex = index;
        cardData = data;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (BattleAutoBoard.Instance != null && cardData != null)
        {
            BattleAutoBoard.Instance.ShowCardPreview(cardData);
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.dragging || BattleAutoBoard.Instance == null) return;

        if (eventData.button == PointerEventData.InputButton.Left)
        {
            BattleAutoBoard.Instance.RequestSummonFromHand(handIndex, BattlePosition.FaceUpAttack);
        }
        else if (eventData.button == PointerEventData.InputButton.Right)
        {
            BattleAutoBoard.Instance.RequestSummonFromHand(handIndex, BattlePosition.FaceDownDefense);
        }
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        originalParent = transform.parent;
        originalSiblingIndex = transform.GetSiblingIndex();
        originalAnchoredPos = rectTransform.anchoredPosition;

        Canvas rootCanvas = GetComponentInParent<Canvas>();
        if (rootCanvas != null)
        {
            transform.SetParent(rootCanvas.transform, true);
        }
        if (canvasGroup != null) canvasGroup.blocksRaycasts = false;
    }

    public void OnDrag(PointerEventData eventData)
    {
        rectTransform.position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (canvasGroup != null) canvasGroup.blocksRaycasts = true;

        int targetZone = -1;
        if (BattleAutoBoard.Instance != null)
        {
            targetZone = BattleAutoBoard.Instance.GetZoneIndexAtScreenPoint(eventData.position, eventData.pressEventCamera);
        }

        transform.SetParent(originalParent, false);
        transform.SetSiblingIndex(originalSiblingIndex);
        rectTransform.anchoredPosition = originalAnchoredPos;

        if (BattleAutoBoard.Instance != null && (targetZone >= 0 || eventData.position.y > Screen.height * 0.25f))
        {
            BattlePosition pos = (eventData.button == PointerEventData.InputButton.Right)
                ? BattlePosition.FaceDownDefense
                : BattlePosition.FaceUpAttack;

            BattleAutoBoard.Instance.RequestSummonFromHand(handIndex, pos, targetZone);
        }
    }
}