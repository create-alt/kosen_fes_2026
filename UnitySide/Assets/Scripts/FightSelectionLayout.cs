using System;
using UnityEngine;
using UnityEngine.UI;

public static class FightSelectionLayout
{
    private static readonly Color Background = new Color(0.025f, 0.035f, 0.06f);
    private static readonly Color Surface = new Color(0.07f, 0.095f, 0.14f);
    private static readonly Color Muted = new Color(0.62f, 0.7f, 0.8f);
    private static readonly Color Gold = new Color(1f, 0.72f, 0.26f);

    public static Color EnemyAccent(GameObject enemy)
    {
        if (enemy != null && enemy.GetComponentInChildren<HybridBossAttack>(true) != null) return Gold;
        if (enemy != null && enemy.GetComponentInChildren<DilatantEnemy>(true) != null) return new Color(0.15f, 0.85f, 0.85f);
        return new Color(1f, 0.35f, 0.3f);
    }

    public static void Apply(FightGameUI.Views view, EnemySelectionController selection, Action<GameObject> created = null, Action<Transform, Transform> reparent = null)
    {
        if (view == null || view.setup == null) return;
        RectTransform setup = view.setup.GetComponent<RectTransform>();
        if (setup == null) return;
        Canvas canvas = setup.GetComponentInParent<Canvas>();
        CanvasScaler scaler = canvas != null ? canvas.GetComponent<CanvasScaler>() : null;
        if (scaler != null)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
        }
        Place(setup, setup.parent, 0f, 0f, 1f, 1f, reparent);
        Image backdrop = setup.GetComponent<Image>();
        if (backdrop != null) backdrop.color = Background;
        Font font = view.chosenEnemy != null && view.chosenEnemy.font != null ? view.chosenEnemy.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        RectTransform root = Panel(setup, "FightSelectionLayout", Background, created);
        Place(root, setup, 0f, 0f, 1f, 1f, reparent);
        Label(root, "Title", "対戦相手を選択", font, 46, Color.white, 0.04f, 0.895f, 0.65f, 0.97f, created);
        Label(root, "Subtitle", "相手を選んで、バトルを始めよう。", font, 24, Muted, 0.04f, 0.845f, 0.65f, 0.89f, created);
        RectTransform cards = Panel(root, "EnemyCards", Color.clear, created);
        Place(cards, root, 0.04f, 0.26f, 0.655f, 0.805f, reparent);
        for (int index = 0; index < view.enemyCards.Length; index++)
        {
            Button card = view.enemyCards[index];
            if (card == null) continue;
            float width = 1f / Mathf.Max(1, view.enemyCards.Length);
            Place(card.GetComponent<RectTransform>(), cards, index * width, 0f, (index + 1) * width - 0.018f, 1f, reparent);
            StyleCard(view, selection, index, font, created, reparent);
        }
        MoveText(view.chosenEnemy, root, 0.04f, 0.17f, 0.655f, 0.23f, 26, Color.white, TextAnchor.MiddleLeft, reparent);
        PlaceButton(view.start, root, 0.04f, 0.055f, 0.643f, 0.145f, "この相手と対戦する", 32, Gold, reparent);
        if (view.start != null)
        {
            foreach (Text text in view.start.GetComponentsInChildren<Text>(true)) text.color = Background;
        }

        RectTransform settings = Panel(root, "BattleSettings", Surface, created);
        Place(settings, root, 0.69f, 0.055f, 0.96f, 0.805f, reparent);
        Label(settings, "Heading", "バトル設定", font, 32, Color.white, 0.065f, 0.91f, 0.94f, 0.98f, created);
        Label(settings, "DifficultyLabel", "難易度", font, 23, Muted, 0.065f, 0.835f, 0.94f, 0.895f, created);
        string[] difficultyNames = { "ゆったり", "標準", "ハード" };
        for (int index = 0; index < view.difficultyButtons.Length; index++)
        {
            float width = 0.87f / Mathf.Max(1, view.difficultyButtons.Length);
            Button button = view.difficultyButtons[index];
            PlaceButton(button, settings, 0.065f + index * width, 0.755f, 0.065f + (index + 1) * width - 0.012f, 0.825f, difficultyNames[Mathf.Min(index, 2)], 22, Background, reparent);
            if (button != null && index < view.difficultyFaces.Length && button.GetComponent<Image>() != null) view.difficultyFaces[index] = button.GetComponent<Image>();
        }
        MoveText(view.difficultyHelp, settings, 0.065f, 0.625f, 0.94f, 0.745f, 22, Muted, TextAnchor.UpperLeft, reparent);
        HealthRow(settings, "PlayerHealth", "自分の体力", view.playerHealth, view.playerHealthMinus, view.playerHealthPlus, 0.47f, font, created, reparent);
        HealthRow(settings, "EnemyHealth", "相手の体力", view.enemyHealth, view.enemyHealthMinus, view.enemyHealthPlus, 0.305f, font, created, reparent);
        Label(settings, "VolumeLabel", "音量", font, 23, Muted, 0.065f, 0.23f, 0.65f, 0.285f, created);
        MoveText(view.volumeValue, settings, 0.72f, 0.23f, 0.94f, 0.285f, 23, Color.white, TextAnchor.MiddleRight, reparent);
        if (view.volume != null) Place(view.volume.GetComponent<RectTransform>(), settings, 0.075f, 0.17f, 0.925f, 0.22f, reparent);
        if (view.keyboard != null)
        {
            Place(view.keyboard.GetComponent<RectTransform>(), settings, 0.065f, 0.055f, 0.94f, 0.135f, reparent);
            Text label = view.keyboard.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = "キーボードテスト";
                MoveText(label, view.keyboard.transform, 0.14f, 0f, 1f, 1f, 23, Color.white, TextAnchor.MiddleLeft, reparent);
            }
            Transform box = view.keyboard.transform.Find("Background");
            if (box is RectTransform boxRect) Place(boxRect, view.keyboard.transform, 0f, 0.25f, 0.085f, 0.75f, reparent);
        }
        MoveText(view.inputHelp, root, 0.69f, 0.845f, 0.96f, 0.97f, 21, Muted, TextAnchor.MiddleLeft, reparent);
        foreach (Transform child in setup)
        {
            if (child != root) child.gameObject.SetActive(false);
        }
        root.SetAsLastSibling();
    }

    private static void StyleCard(FightGameUI.Views view, EnemySelectionController selection, int index, Font font, Action<GameObject> created, Action<Transform, Transform> reparent)
    {
        Button button = view.enemyCards[index];
        RectTransform card = button.GetComponent<RectTransform>();
        GameObject enemy = selection != null ? selection.GetEnemy(index) : null;
        bool hybrid = enemy != null && enemy.GetComponentInChildren<HybridBossAttack>(true) != null;
        bool liquid = enemy != null && enemy.GetComponentInChildren<DilatantEnemy>(true) != null;
        Color accent = EnemyAccent(enemy);
        Image face = card.GetComponent<Image>();
        if (face != null)
        {
            face.color = Surface;
            face.raycastTarget = true;
            button.targetGraphic = face;
            if (index < view.enemyFaces.Length) view.enemyFaces[index] = face;
        }
        SetButtonColors(button);
        RectTransform content = Panel(card, "CardContent", Color.clear, created);
        Place(content, card, 0f, 0f, 1f, 1f, reparent);
        Label(content, "Category", (index + 1).ToString("00") + "  /  " + (hybrid ? "高難易度" : liquid ? "液体型" : "ボクシング型"), font, 21, accent, 0.08f, 0.87f, 0.92f, 0.96f, created);
        Label(content, "Name", hybrid ? "HYBRID\nOVERDRIVE" : liquid ? "DILATANT\nSENTINEL" : "PUNCHBOT", font, 39, Color.white, 0.08f, 0.64f, 0.94f, 0.85f, created);
        Label(content, "HintTitle", "回避のポイント", font, 21, Muted, 0.08f, 0.54f, 0.92f, 0.6f, created);
        string description = hybrid ? "波＋3～5連射＋パンチ\n左右移動としゃがみ\n近づいたら後退" : liquid ? "波はしゃがんで回避\n正面弾は左右移動\n近距離は後退" : "ミサイルは左右移動\n近距離パンチは後退\n予測線を見て回避";
        Label(content, "Description", description, font, 25, Color.white, 0.08f, 0.27f, 0.92f, 0.52f, created);
        RectTransform accentLine = Panel(content, "Accent", accent, created);
        Place(accentLine, content, 0.08f, 0.22f, 0.92f, 0.228f, reparent);
        GameObject mark = index < view.selectedMarks.Length ? view.selectedMarks[index] : null;
        if (mark != null)
        {
            Place(mark.GetComponent<RectTransform>(), card, 0.08f, 0.055f, 0.92f, 0.17f, reparent);
            Image markerImage = mark.GetComponent<Image>();
            if (markerImage != null)
            {
                markerImage.color = new Color(accent.r, accent.g, accent.b, 0.2f);
                markerImage.raycastTarget = false;
            }
            Text markerText = mark.GetComponent<Text>();
            if (markerText != null)
            {
                markerText.text = "選択中";
                markerText.fontSize = 22;
                markerText.color = accent;
                markerText.alignment = TextAnchor.MiddleCenter;
                markerText.raycastTarget = false;
            }
            else
            {
                Label(mark.transform, "SelectionLabel", "選択中", font, 22, accent, 0f, 0f, 1f, 1f, created);
                mark.transform.Find("SelectionLabel").GetComponent<Text>().alignment = TextAnchor.MiddleCenter;
            }
        }
        foreach (Transform child in card)
        {
            if (child != content && (mark == null || child != mark.transform)) child.gameObject.SetActive(false);
        }
    }

    private static void HealthRow(RectTransform parent, string name, string title, InputField input, Button minus, Button plus, float bottom, Font font, Action<GameObject> created, Action<Transform, Transform> reparent)
    {
        Label(parent, name + "Label", title, font, 23, Muted, 0.065f, bottom + 0.09f, 0.94f, bottom + 0.145f, created);
        PlaceButton(minus, parent, 0.065f, bottom, 0.235f, bottom + 0.08f, "−", 30, Background, reparent);
        PlaceButton(plus, parent, 0.77f, bottom, 0.94f, bottom + 0.08f, "+", 30, Background, reparent);
        if (input == null) return;
        Place(input.GetComponent<RectTransform>(), parent, 0.26f, bottom, 0.745f, bottom + 0.08f, reparent);
        Image image = input.GetComponent<Image>();
        if (image != null) image.color = Background;
        if (input.textComponent != null)
        {
            input.textComponent.fontSize = 28;
            input.textComponent.alignment = TextAnchor.MiddleCenter;
            input.textComponent.color = Color.white;
            input.textComponent.resizeTextForBestFit = false;
        }
    }

    private static RectTransform Panel(Transform parent, string name, Color color, Action<GameObject> created)
    {
        Transform existing = parent.Find(name);
        GameObject element = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        if (existing == null)
        {
            element.transform.SetParent(parent, false);
            element.layer = parent.gameObject.layer;
            created?.Invoke(element);
        }
        Image image = element.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return (RectTransform)element.transform;
    }

    private static void Label(Transform parent, string name, string value, Font font, int size, Color color, float left, float bottom, float right, float top, Action<GameObject> created)
    {
        Transform existing = parent.Find(name);
        GameObject element = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        if (existing == null)
        {
            element.transform.SetParent(parent, false);
            element.layer = parent.gameObject.layer;
            created?.Invoke(element);
        }
        Text text = element.GetComponent<Text>();
        text.font = font;
        text.text = value;
        MoveText(text, parent, left, bottom, right, top, size, color, TextAnchor.MiddleLeft, null);
    }

    private static void MoveText(Text text, Transform parent, float left, float bottom, float right, float top, int size, Color color, TextAnchor alignment, Action<Transform, Transform> reparent)
    {
        if (text == null) return;
        Place(text.rectTransform, parent, left, bottom, right, top, reparent);
        text.fontSize = size;
        text.resizeTextForBestFit = false;
        text.alignment = alignment;
        text.color = color;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.supportRichText = false;
    }

    private static void PlaceButton(Button button, Transform parent, float left, float bottom, float right, float top, string label, int size, Color color, Action<Transform, Transform> reparent)
    {
        if (button == null) return;
        Place(button.GetComponent<RectTransform>(), parent, left, bottom, right, top, reparent);
        Image image = button.GetComponent<Image>();
        if (image != null)
        {
            image.color = color;
            image.raycastTarget = true;
            button.targetGraphic = image;
        }
        foreach (Text text in button.GetComponentsInChildren<Text>(true))
        {
            text.text = label;
            MoveText(text, text.transform.parent, 0.04f, 0f, 0.96f, 1f, size, Color.white, TextAnchor.MiddleCenter, reparent);
        }
        SetButtonColors(button);
    }

    private static void SetButtonColors(Button button)
    {
        button.transition = Selectable.Transition.ColorTint;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
        colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
        colors.selectedColor = Color.white;
        colors.fadeDuration = 0.08f;
        button.colors = colors;
    }

    private static void Place(RectTransform rect, Transform parent, float left, float bottom, float right, float top, Action<Transform, Transform> reparent)
    {
        if (rect == null) return;
        if (rect.parent != parent)
        {
            if (reparent != null) reparent(rect, parent);
            else rect.SetParent(parent, false);
        }
        rect.anchorMin = new Vector2(left, bottom);
        rect.anchorMax = new Vector2(right, top);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = Vector2.zero;
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;
    }
}
