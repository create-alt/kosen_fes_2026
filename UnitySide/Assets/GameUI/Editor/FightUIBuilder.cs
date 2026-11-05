using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class FightUIBuilder
{
    private static readonly Color Background = Hex("09111F");
    private static readonly Color PanelColor = Hex("131E2E");
    private static readonly Color Accent = Hex("29DFC6");
    private static readonly Color White = Hex("EAF1F8");
    private static readonly Color Muted = Hex("91A5BC");
    private static Font font;
    private static Sprite whiteSprite;

    [MenuItem("Tools/Fight Engineering/Build Game UI")]
    public static void BuildCurrentScene()
    {
        Build(SceneManager.GetActiveScene());
    }

    public static FightGameUI Build(Scene scene)
    {
        font = AssetDatabase.LoadAssetAtPath<Font>("Assets/GameUI/Fonts/NotoSansJP.ttf");
        if (font == null) throw new Exception("Japanese UI font is missing.");
        whiteSprite = EnsureWhiteSprite();
        PlayerController player = Components<PlayerController>(scene).FirstOrDefault();
        EnemySelectionController selection = Components<EnemySelectionController>(scene).FirstOrDefault();
        if (player == null || selection == null || selection.EnemyCount < 2) throw new Exception("Player and two configured enemy choices are required.");
        var selectionData = new SerializedObject(selection);
        GameObject oldPanel = selectionData.FindProperty("selectionPanel").objectReferenceValue as GameObject;
        if (oldPanel != null) oldPanel.SetActive(false);
        foreach (FightGameUI existing in Components<FightGameUI>(scene)) Undo.DestroyObjectImmediate(existing.gameObject);
        foreach (BattleHealthDisplay legacy in Components<BattleHealthDisplay>(scene)) legacy.gameObject.SetActive(false);

        Canvas[] canvases = new Canvas[3];
        for (int display = 0; display < 3; display++)
        {
            canvases[display] = Components<Canvas>(scene).FirstOrDefault(canvas => canvas.isRootCanvas && canvas.targetDisplay == display);
            if (canvases[display] == null)
            {
                GameObject canvasObject = new GameObject("FightCanvas" + display, typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(canvasObject, scene);
                canvases[display] = canvasObject.GetComponent<Canvas>();
            }
            Canvas canvas = canvases[display];
            canvas.gameObject.layer = 5;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = display;
            canvas.enabled = true;
            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>() ?? canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            if (canvas.GetComponent<GraphicRaycaster>() == null) canvas.gameObject.AddComponent<GraphicRaycaster>();
            Transform oldHud = canvas.transform.Find("BattleHUD");
            if (oldHud != null) Undo.DestroyObjectImmediate(oldHud.gameObject);
            Transform oldLines = canvas.transform.Find("AttackPredictions");
            if (oldLines != null) Undo.DestroyObjectImmediate(oldLines.gameObject);
            RectTransform lines = Rect(canvas.transform, "AttackPredictions", 0f, 0f, 1f, 1f);
            Image template = Image(lines, "PredictionLineTemplate", 0f, 0f, 1f, 1f, Color.white);
            template.gameObject.SetActive(false);
            foreach (Camera camera in Components<Camera>(scene).Where(item => item.targetDisplay == display))
            {
                LiquidAttackPrediction liquid = camera.GetComponent<LiquidAttackPrediction>() ?? camera.gameObject.AddComponent<LiquidAttackPrediction>();
                MissileAttackPrediction missiles = camera.GetComponent<MissileAttackPrediction>() ?? camera.gameObject.AddComponent<MissileAttackPrediction>();
                SetObject(liquid, "predictionLineTemplate", template);
                SetObject(missiles, "predictionLineTemplate", template);
            }
        }

        RectTransform uiRoot = Rect(canvases[0].transform, "FightGameUI", 0f, 0f, 1f, 1f);
        FightGameUI ui = uiRoot.gameObject.AddComponent<FightGameUI>();
        FightGameUI.Views views = new FightGameUI.Views();
        BuildSetup(uiRoot, views);
        views.huds = canvases.Select(canvas => BuildHud(canvas.transform)).ToArray();
        views.battleMenu = Button(views.huds[0].root.transform, "BattleMenu", "敵選択へ戻る", 0.83f, 0.025f, 0.98f, 0.085f, PanelColor, 22);
        RectTransform rush = Rect(uiRoot, "FinisherRush", 0.25f, 0.57f, 0.75f, 0.84f);
        rush.gameObject.AddComponent<Image>().color = new Color(0.04f, 0.07f, 0.12f, 0.94f);
        views.rush = rush.gameObject;
        Label(rush, "RushTitle", "FINISH RUSH", 0.05f, 0.72f, 0.95f, 0.96f, 38, Accent, FontStyle.Bold).alignment = TextAnchor.MiddleCenter;
        views.rushCount = Label(rush, "RushCount", "連打！ 0 / 8", 0.05f, 0.34f, 0.95f, 0.72f, 52, White, FontStyle.Bold);
        views.rushCount.alignment = TextAnchor.MiddleCenter;
        views.rushTime = Label(rush, "RushTime", "5.0 秒", 0.05f, 0.13f, 0.95f, 0.34f, 28, Accent);
        views.rushTime.alignment = TextAnchor.MiddleCenter;
        Label(rush, "RushHelp", "その場でパンチを連打！", 0.05f, 0.01f, 0.95f, 0.14f, 19, White).alignment = TextAnchor.MiddleCenter;
        views.rush.SetActive(false);
        BattleAudio audio = uiRoot.gameObject.AddComponent<BattleAudio>();
        audio.Configure(player, System.Enum.GetValues(typeof(FightSound)).Cast<FightSound>().Select(sound => new BattleAudio.SoundClip { sound = sound, clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/GameAudio/Clips/" + sound + ".wav") }).ToArray());
        BuildResult(uiRoot, views);
        uiRoot.SetAsLastSibling();
        ui.Configure(player, selection, views);
        selectionData.Update();
        selectionData.FindProperty("selectionPanel").objectReferenceValue = views.setup;
        selectionData.FindProperty("requireStartButton").boolValue = true;
        SerializedProperty choices = selectionData.FindProperty("enemyChoices");
        for (int index = 0; index < Mathf.Min(choices.arraySize, views.enemyCards.Length); index++) choices.GetArrayElementAtIndex(index).FindPropertyRelative("selectButton").objectReferenceValue = views.enemyCards[index];
        selectionData.ApplyModifiedPropertiesWithoutUndo();

        AudioListener mainListener = Components<Camera>(scene).Where(camera => camera.targetDisplay == 0).Select(camera => camera.GetComponent<AudioListener>()).FirstOrDefault(listener => listener != null);
        if (mainListener == null)
        {
            Camera mainCamera = Components<Camera>(scene).FirstOrDefault(camera => camera.targetDisplay == 0);
            if (mainCamera != null) mainListener = mainCamera.gameObject.AddComponent<AudioListener>();
        }
        foreach (AudioListener listener in Components<AudioListener>(scene)) listener.enabled = listener == mainListener;
        views.result.SetActive(false);
        foreach (FightGameUI.Hud hud in views.huds) hud.root.SetActive(false);
        views.setup.SetActive(true);
        EditorUtility.SetDirty(ui);
        EditorSceneManager.MarkSceneDirty(scene);
        return ui;
    }

    private static void BuildSetup(Transform parent, FightGameUI.Views views)
    {
        Image background = Image(parent, "BattleSetup", 0f, 0f, 1f, 1f, Background);
        background.raycastTarget = true;
        Transform panel = background.transform;
        views.setup = background.gameObject;
        Image(panel, "TopAccent", 0.04f, 0.972f, 0.17f, 0.979f, Accent);
        Label(panel, "Title", "FIGHT ENGINEERING", 0.04f, 0.865f, 0.9f, 0.965f, 58, White, FontStyle.Bold);
        Label(panel, "Subtitle", "対戦相手を選んで、準備を整えよう。", 0.04f, 0.814f, 0.61f, 0.866f, 26, Muted);
        Label(panel, "Rules", "最大HPは対戦設定で変更できます\n攻撃 10 固定 / 敵HPに応じて3段階", 0.65f, 0.814f, 0.96f, 0.88f, 20, Muted);
        views.enemyCards = new Button[2];
        views.enemyFaces = new Image[2];
        views.selectedMarks = new GameObject[2];
        for (int index = 0; index < 2; index++)
        {
            float left = index == 0 ? 0.04f : 0.33f;
            Color color = index == 0 ? Accent : Hex("FFAA4F");
            Button card = Button(panel, "EnemyCard" + index, "", left, 0.335f, left + 0.27f, 0.78f, index == 0 ? Hex("143B42") : PanelColor, 24);
            Transform cardRoot = card.transform;
            views.enemyCards[index] = card;
            views.enemyFaces[index] = card.GetComponent<Image>();
            GameObject mark = Image(cardRoot, "SelectedAccent", 0f, 0.985f, 1f, 1f, color).gameObject;
            views.selectedMarks[index] = mark;
            mark.SetActive(index == 0);
            Label(cardRoot, "Number", index == 0 ? "01" : "02", 0.7f, 0.02f, 0.98f, 0.28f, 72, new Color(color.r, color.g, color.b, 0.12f), FontStyle.Bold);
            Label(cardRoot, "Role", index == 0 ? "BOXING TYPE" : "LIQUID TYPE", 0.08f, 0.82f, 0.95f, 0.92f, 19, color, FontStyle.Bold);
            Label(cardRoot, "EnemyName", index == 0 ? "PUNCHBOT" : "DILATANT\nSENTINEL", 0.08f, 0.53f, 0.94f, 0.82f, 40, White, FontStyle.Bold);
            Label(cardRoot, "Description", index == 0 ? "接近するとボクシング。\n離れるとミサイルで攻撃。" : "連打で硬化する液体の敵。\n3発打ったら、ひと呼吸。", 0.08f, 0.3f, 0.94f, 0.5f, 24, White);
            Image(cardRoot, "Divider", 0.08f, 0.26f, 0.92f, 0.264f, Hex("304252"));
            Label(cardRoot, "Strategy", index == 0 ? "回避：前後移動・ジャンプ" : "回避：前後移動・しゃがみ", 0.08f, 0.1f, 0.92f, 0.22f, 21, Muted);
        }
        views.chosenEnemy = Label(panel, "ChosenEnemy", "選択中：PUNCHBOT", 0.04f, 0.263f, 0.6f, 0.313f, 25, Accent);
        views.start = Button(panel, "StartBattle", "対戦をはじめる", 0.04f, 0.13f, 0.6f, 0.235f, Hex("128B7D"), 34);
        views.inputHelp = Label(panel, "InputHelp", "Space：パンチ   W / S：前後移動\n↑ / ↓：高さ   J：しゃがむ   Home：姿勢リセット", 0.04f, 0.03f, 0.6f, 0.105f, 21, Muted);

        Transform settings = Image(panel, "SettingsPanel", 0.64f, 0.13f, 0.96f, 0.78f, PanelColor).transform;
        Label(settings, "SettingsTitle", "対戦設定", 0.06f, 0.9f, 0.94f, 0.97f, 30, White, FontStyle.Bold);
        Label(settings, "DifficultyLabel", "難易度", 0.06f, 0.82f, 0.94f, 0.88f, 23, Muted);
        views.difficultyButtons = new Button[3];
        views.difficultyFaces = new Image[3];
        string[] names = { "ゆっくり", "標準", "チャレンジ" };
        for (int index = 0; index < 3; index++)
        {
            float left = 0.06f + index * 0.3f;
            Button button = Button(settings, "Difficulty" + index, names[index], left, 0.73f, left + 0.28f, 0.81f, index == 1 ? Hex("145A55") : Hex("203146"), 21);
            views.difficultyButtons[index] = button;
            views.difficultyFaces[index] = button.GetComponent<Image>();
        }
        views.difficultyHelp = Label(settings, "DifficultyHelp", "敵HPに合わせて3段階で変化します。", 0.06f, 0.66f, 0.94f, 0.72f, 20, Muted);
        Label(settings, "HealthLabel", "最大HP（10〜1000）", 0.06f, 0.59f, 0.94f, 0.65f, 21, Muted);
        views.playerHealth = HealthInput(settings, "PlayerMaxHealth", "Player HP", 0.48f, 0.57f, out views.playerHealthMinus, out views.playerHealthPlus);
        views.enemyHealth = HealthInput(settings, "EnemyMaxHealth", "敵 HP", 0.37f, 0.46f, out views.enemyHealthMinus, out views.enemyHealthPlus);
        views.keyboard = Toggle(settings, "KeyboardToggle", 0.06f, 0.25f, 0.94f, 0.33f);
        Label(settings, "VolumeLabel", "音量", 0.06f, 0.17f, 0.65f, 0.23f, 23, Muted);
        views.volumeValue = Label(settings, "VolumeValue", "80%", 0.68f, 0.17f, 0.94f, 0.23f, 23, White);
        views.volumeValue.alignment = TextAnchor.MiddleRight;
        views.volume = Slider(settings, "Volume", 0.06f, 0.06f, 0.94f, 0.15f);
        Label(panel, "SavedSettingsHint", "対戦開始時に設定を保存します。", 0.64f, 0.04f, 0.96f, 0.1f, 18, Muted);
    }

    private static FightGameUI.Hud BuildHud(Transform canvas)
    {
        RectTransform root = Rect(canvas, "BattleHUD", 0f, 0f, 1f, 1f);
        Image(root, "Header", 0.02f, 0.865f, 0.98f, 0.99f, new Color(0.025f, 0.045f, 0.08f, 0.9f));
        Label(root, "PlayerName", "PLAYER", 0.04f, 0.943f, 0.25f, 0.979f, 23, Accent, FontStyle.Bold);
        Text playerValue = Label(root, "PlayerHealthValue", "100 / 100", 0.27f, 0.943f, 0.43f, 0.979f, 23, White);
        playerValue.alignment = TextAnchor.MiddleRight;
        Transform playerBar = Image(root, "PlayerHealthTrack", 0.04f, 0.895f, 0.43f, 0.928f, Hex("243348")).transform;
        Image playerFill = Image(playerBar, "PlayerHealthFill", 0f, 0f, 1f, 1f, Accent);
        playerFill.type = UnityEngine.UI.Image.Type.Filled;
        playerFill.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
        Text enemyName = Label(root, "EnemyName", "PUNCHBOT", 0.57f, 0.943f, 0.84f, 0.979f, 22, White, FontStyle.Bold);
        Text enemyValue = Label(root, "EnemyHealthValue", "100 / 100", 0.85f, 0.943f, 0.96f, 0.979f, 23, White);
        enemyValue.alignment = TextAnchor.MiddleRight;
        Transform enemyBar = Image(root, "EnemyHealthTrack", 0.57f, 0.895f, 0.96f, 0.928f, Hex("243348")).transform;
        Image enemyFill = Image(enemyBar, "EnemyHealthFill", 0f, 0f, 1f, 1f, Hex("FF574C"));
        enemyFill.type = UnityEngine.UI.Image.Type.Filled;
        enemyFill.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
        enemyFill.fillOrigin = (int)UnityEngine.UI.Image.OriginHorizontal.Right;
        Text stage = Label(root, "Stage", "STAGE 1", 0.435f, 0.937f, 0.565f, 0.981f, 25, White, FontStyle.Bold);
        stage.alignment = TextAnchor.MiddleCenter;
        Image[] marks = new Image[3];
        for (int index = 0; index < marks.Length; index++) marks[index] = Image(root, "StageMark" + index, 0.455f + index * 0.033f, 0.9f, 0.479f + index * 0.033f, 0.915f, index == 0 ? Accent : Hex("334254"));
        Text status = Label(root, "BattleStatus", "", 0.35f, 0.79f, 0.65f, 0.85f, 34, Accent, FontStyle.Bold);
        status.alignment = TextAnchor.MiddleCenter;
        return new FightGameUI.Hud { root = root.gameObject, playerFill = playerFill, enemyFill = enemyFill, playerValue = playerValue, enemyValue = enemyValue, enemyName = enemyName, stage = stage, status = status, phaseMarks = marks };
    }

    private static void BuildResult(Transform parent, FightGameUI.Views views)
    {
        Image backdrop = Image(parent, "BattleResult", 0f, 0f, 1f, 1f, new Color(0.015f, 0.025f, 0.045f, 0.82f));
        backdrop.raycastTarget = true;
        views.result = backdrop.gameObject;
        Transform panel = Image(backdrop.transform, "ResultCard", 0.27f, 0.25f, 0.73f, 0.75f, PanelColor).transform;
        Image(panel, "Accent", 0f, 0.985f, 1f, 1f, Accent);
        views.resultTitle = Label(panel, "Outcome", "VICTORY", 0.08f, 0.64f, 0.92f, 0.86f, 68, Accent, FontStyle.Bold);
        views.resultTitle.alignment = TextAnchor.MiddleCenter;
        views.resultDetail = Label(panel, "OutcomeDetail", "相手を倒しました！", 0.08f, 0.47f, 0.92f, 0.62f, 26, White);
        views.resultDetail.alignment = TextAnchor.MiddleCenter;
        views.retry = Button(panel, "Retry", "もう一度対戦", 0.07f, 0.23f, 0.48f, 0.39f, Hex("128B7D"), 27);
        views.resultMenu = Button(panel, "ChooseAnotherEnemy", "敵選択へ戻る", 0.52f, 0.23f, 0.93f, 0.39f, Hex("2B3E55"), 27);
        Text hint = Label(panel, "RestartHint", "Rキーでも再戦できます", 0.1f, 0.08f, 0.9f, 0.19f, 21, Muted);
        hint.alignment = TextAnchor.MiddleCenter;
    }

    private static Toggle Toggle(Transform parent, string name, float x0, float y0, float x1, float y1)
    {
        RectTransform root = Rect(parent, name, x0, y0, x1, y1);
        Image clickArea = root.gameObject.AddComponent<Image>();
        clickArea.color = Color.clear;
        clickArea.raycastTarget = true;
        Toggle toggle = root.gameObject.AddComponent<Toggle>();
        Image box = Image(root, "Box", 0f, 0.1f, 0.105f, 0.9f, Hex("2D4459"));
        box.raycastTarget = true;
        Image check = Image(box.transform, "Check", 0.22f, 0.22f, 0.78f, 0.78f, Accent);
        toggle.targetGraphic = box;
        toggle.graphic = check;
        toggle.isOn = true;
        Label(root, "Label", "キーボードでテスト", 0.15f, 0f, 1f, 1f, 24, White);
        return toggle;
    }

    private static InputField HealthInput(Transform parent, string name, string label, float y0, float y1, out Button minus, out Button plus)
    {
        Label(parent, name + "Label", label, 0.06f, y0, 0.35f, y1, 23, White);
        Image background = Image(parent, name, 0.38f, y0, 0.7f, y1, Hex("263A50"));
        background.raycastTarget = true;
        InputField input = background.gameObject.AddComponent<InputField>();
        Text text = Label(background.transform, "Value", "100", 0.06f, 0.03f, 0.94f, 0.97f, 26, White);
        text.alignment = TextAnchor.MiddleCenter;
        input.targetGraphic = background;
        input.textComponent = text;
        input.contentType = InputField.ContentType.IntegerNumber;
        input.characterLimit = 4;
        input.customCaretColor = true;
        input.caretColor = White;
        input.selectionColor = new Color(0.16f, 0.88f, 0.78f, 0.35f);
        input.text = "100";
        minus = Button(parent, name + "Minus", "-", 0.74f, y0, 0.83f, y1, Hex("2B3E55"), 27);
        plus = Button(parent, name + "Plus", "+", 0.85f, y0, 0.94f, y1, Hex("2B3E55"), 27);
        return input;
    }

    private static Slider Slider(Transform parent, string name, float x0, float y0, float x1, float y1)
    {
        RectTransform root = Rect(parent, name, x0, y0, x1, y1);
        Slider slider = root.gameObject.AddComponent<Slider>();
        Image track = Image(root, "Track", 0f, 0.38f, 1f, 0.62f, Hex("30475E"));
        track.raycastTarget = true;
        RectTransform fillArea = Rect(root, "FillArea", 0f, 0.38f, 1f, 0.62f);
        Image fill = Image(fillArea, "Fill", 0f, 0f, 1f, 1f, Accent);
        RectTransform handleArea = Rect(root, "HandleArea", 0f, 0f, 1f, 1f);
        Image handle = Image(handleArea, "Handle", 0.5f, 0.15f, 0.5f, 0.85f, White);
        handle.rectTransform.sizeDelta = new Vector2(18f, 0f);
        handle.raycastTarget = true;
        slider.fillRect = fill.rectTransform;
        slider.handleRect = handle.rectTransform;
        slider.targetGraphic = handle;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = 0.8f;
        return slider;
    }

    private static Button Button(Transform parent, string name, string text, float x0, float y0, float x1, float y1, Color color, int fontSize)
    {
        Image image = Image(parent, name, x0, y0, x1, y1, color);
        image.raycastTarget = true;
        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
        colors.pressedColor = new Color(0.8f, 0.9f, 0.95f);
        button.colors = colors;
        if (!string.IsNullOrEmpty(text))
        {
            Text label = Label(image.transform, "Label", text, 0.04f, 0.06f, 0.96f, 0.94f, fontSize, White, FontStyle.Bold);
            label.alignment = TextAnchor.MiddleCenter;
        }
        return button;
    }

    private static Text Label(Transform parent, string name, string text, float x0, float y0, float x1, float y1, int size, Color color, FontStyle style = FontStyle.Normal)
    {
        RectTransform rect = Rect(parent, name, x0, y0, x1, y1);
        Text label = rect.gameObject.AddComponent<Text>();
        label.font = font;
        label.fontSize = size;
        label.fontStyle = style;
        label.color = color;
        label.text = text;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.raycastTarget = false;
        return label;
    }

    private static Image Image(Transform parent, string name, float x0, float y0, float x1, float y1, Color color)
    {
        Image image = Rect(parent, name, x0, y0, x1, y1).gameObject.AddComponent<Image>();
        image.sprite = whiteSprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static RectTransform Rect(Transform parent, string name, float x0, float y0, float x1, float y1)
    {
        GameObject child = new GameObject(name, typeof(RectTransform));
        child.layer = 5;
        RectTransform rect = child.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(x0, y0);
        rect.anchorMax = new Vector2(x1, y1);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
        return rect;
    }

    private static void SetObject(UnityEngine.Object target, string name, UnityEngine.Object value)
    {
        SerializedObject data = new SerializedObject(target);
        data.FindProperty(name).objectReferenceValue = value;
        data.ApplyModifiedPropertiesWithoutUndo();
    }

    private static Sprite EnsureWhiteSprite()
    {
        const string path = "Assets/GameUI/UIWhite.png";
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite != null) return sprite;
        Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 1f;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
        sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null) throw new Exception("UI fill sprite could not be imported.");
        return sprite;
    }

    private static T[] Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();
    private static Color Hex(string value)
    {
        ColorUtility.TryParseHtmlString("#" + value, out Color color);
        return color;
    }
}
