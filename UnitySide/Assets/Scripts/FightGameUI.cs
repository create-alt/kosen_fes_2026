using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class FightGameUI : MonoBehaviour
{
    [Serializable]
    public sealed class Hud
    {
        public GameObject root;
        public Image playerFill;
        public Image enemyFill;
        public Text playerValue;
        public Text enemyValue;
        public Text enemyName;
        public Text stage;
        public Text status;
        public Image[] phaseMarks;
    }

    [Serializable]
    public sealed class Views
    {
        public GameObject setup;
        public Button[] enemyCards;
        public Image[] enemyFaces;
        public GameObject[] selectedMarks;
        public Text chosenEnemy;
        public Button start;
        public Button[] difficultyButtons;
        public Image[] difficultyFaces;
        public Text difficultyHelp;
        public Toggle keyboard;
        public Slider volume;
        public Text volumeValue;
        public Text inputHelp;
        public InputField playerHealth;
        public InputField enemyHealth;
        public Button playerHealthMinus;
        public Button playerHealthPlus;
        public Button enemyHealthMinus;
        public Button enemyHealthPlus;
        public Hud[] huds;
        public GameObject rush;
        public Text rushCount;
        public Text rushTime;
        public GameObject result;
        public Text resultTitle;
        public Text resultDetail;
        public Button retry;
        public Button resultMenu;
        public Button battleMenu;
    }

    [SerializeField] private PlayerController player;
    [SerializeField] private EnemySelectionController selection;
    [SerializeField] private Views view;
    private CombatFeedback feedback;
    private GameObject trainingPanel;
    private Transform trainingReference;
    private Text trainingStatus;
    private DilatantLiquidAttack trainingAttack;
    private string trainingInstruction;
    private Transform trainingBag;
    private float trainingPunchTime = float.NegativeInfinity;
    private bool resultAnnounced;
    private float resultElapsed;
    private int selectedEnemy;
    private int selectedDifficulty = 1;
    private static readonly Color Accent = new Color(0.16f, 0.88f, 0.78f);
    private static readonly Color Panel = new Color(0.075f, 0.11f, 0.17f);
    private static readonly Color EnemyColor = new Color(1f, 0.34f, 0.3f);

    public void Configure(PlayerController targetPlayer, EnemySelectionController selector, Views references)
    {
        player = targetPlayer;
        selection = selector;
        view = references;
    }

    public void ApplySelectionLayout(Action<GameObject> created = null, Action<Transform, Transform> reparent = null)
    {
        FightSelectionLayout.Apply(view, selection, created, reparent);
    }

    private void Awake()
    {
        if (player == null || selection == null || view == null) return;
        ApplySelectionLayout();
        trainingPanel = FightSelectionLayout.CreateTraining(view, StartTraining, StartTrainingAttack, ReturnToMenu, out trainingStatus);
        player.PunchPressed += OnTrainingPunch;
        BoxingArenaBackdrop.Install(player);
        GameObject overlay = new GameObject("CombatFeedback", typeof(RectTransform), typeof(CanvasRenderer), typeof(CombatFeedback));
        overlay.transform.SetParent(transform, false);
        RectTransform overlayRect = (RectTransform)overlay.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;
        feedback = overlay.GetComponent<CombatFeedback>();
        feedback.Configure(player, view.resultTitle.font);
        FightSettings.Load();
        selectedDifficulty = FightSettings.Difficulty;
        view.keyboard.SetIsOnWithoutNotify(FightSettings.KeyboardTest);
        view.volume.SetValueWithoutNotify(FightSettings.Volume);
        if (view.playerHealth != null)
        {
            view.playerHealth.SetTextWithoutNotify(FightSettings.PlayerHealth.ToString());
            view.enemyHealth.SetTextWithoutNotify(FightSettings.EnemyHealth.ToString());
            view.playerHealth.onEndEdit.AddListener(_ => NormalizeHealth(view.playerHealth, FightSettings.PlayerHealth));
            view.enemyHealth.onEndEdit.AddListener(_ => NormalizeHealth(view.enemyHealth, FightSettings.EnemyHealth));
            view.playerHealthMinus.onClick.AddListener(() => AdjustHealth(view.playerHealth, -10));
            view.playerHealthPlus.onClick.AddListener(() => AdjustHealth(view.playerHealth, 10));
            view.enemyHealthMinus.onClick.AddListener(() => AdjustHealth(view.enemyHealth, -10));
            view.enemyHealthPlus.onClick.AddListener(() => AdjustHealth(view.enemyHealth, 10));
        }
        for (int index = 0; index < view.enemyCards.Length; index++)
        {
            int choice = index;
            view.enemyCards[index].onClick.AddListener(() => ChooseEnemy(choice));
        }
        for (int index = 0; index < view.difficultyButtons.Length; index++)
        {
            int choice = index;
            view.difficultyButtons[index].onClick.AddListener(() => ChooseDifficulty(choice));
        }
        view.start.onClick.AddListener(StartBattle);
        view.retry.onClick.AddListener(Retry);
        view.resultMenu.onClick.AddListener(ReturnToMenu);
        view.battleMenu.onClick.AddListener(ReturnToMenu);
        view.keyboard.onValueChanged.AddListener(_ => RefreshSettings());
        view.volume.onValueChanged.AddListener(UpdateVolume);
        ChooseEnemy(0);
        RefreshSettings();
        view.result.SetActive(false);
    }

    public void ChooseEnemy(int index)
    {
        if (selection == null || index < 0 || index >= selection.EnemyCount) return;
        selectedEnemy = index;
        for (int choice = 0; choice < view.enemyFaces.Length; choice++)
        {
            view.enemyFaces[choice].color = choice == index ? Color.Lerp(Panel, FightSelectionLayout.EnemyAccent(selection.GetEnemy(choice)), 0.2f) : Panel;
            view.selectedMarks[choice].SetActive(choice == index);
        }
        view.chosenEnemy.text = "選択中：" + EnemyName(selection.GetEnemy(index));
        RefreshSettings();
    }

    public void ChooseDifficulty(int index)
    {
        selectedDifficulty = Mathf.Clamp(index, 0, 2);
        RefreshSettings();
    }

    private void RefreshSettings()
    {
        for (int index = 0; index < view.difficultyFaces.Length; index++) view.difficultyFaces[index].color = index == selectedDifficulty ? new Color(0.08f, 0.36f, 0.34f) : new Color(0.12f, 0.18f, 0.27f);
        view.difficultyHelp.text = selectedDifficulty == 0 ? "予告は長め。連射は1発ずつ。" : selectedDifficulty == 2 ? "速めのテンポ。連射が増えます。" : "敵HPに合わせて3段階で変化します。";
        GameObject chosen = selection.GetEnemy(selectedEnemy);
        if (chosen != null && chosen.GetComponentInChildren<HybridBossAttack>(true) != null)
        {
            view.difficultyHelp.text = selectedDifficulty == 0 ? "高難易度の敵です。予告と休憩を長めにします。" : selectedDifficulty == 2 ? "短い予告で波と最大5連射が重なります。" : "しゃがみ波と3～5連射。HP低下で複合攻撃になります。";
        }
        view.volumeValue.text = Mathf.RoundToInt(view.volume.value * 100f) + "%";
        view.inputHelp.text = view.keyboard.isOn ? "Space：パンチ   W / S：前後   A / D：左右\n↑ / ↓：高さ   J：しゃがむ   Home：姿勢リセット" : "画像認識からの動きで操作します。\nキーボードのテスト操作はOFFです。";
    }

    private void SaveSettings()
    {
        FightSettings.Set(selectedDifficulty, view.keyboard.isOn, view.volume.value);
        int playerHealth = ReadHealth(view.playerHealth, FightSettings.PlayerHealth);
        int enemyHealth = ReadHealth(view.enemyHealth, FightSettings.EnemyHealth);
        FightSettings.SetHealth(playerHealth, enemyHealth);
        NormalizeHealth(view.playerHealth, playerHealth);
        NormalizeHealth(view.enemyHealth, enemyHealth);
        FightSettings.Save();
        player.SetKeyboardTestEnabled(view.keyboard.isOn);
    }

    private static int ReadHealth(InputField field, int fallback)
    {
        return field != null && int.TryParse(field.text, out int value) ? FightSettings.ClampHealth(value) : fallback;
    }

    private static void NormalizeHealth(InputField field, int fallback)
    {
        if (field != null) field.SetTextWithoutNotify(ReadHealth(field, fallback).ToString());
    }

    private static void AdjustHealth(InputField field, int change)
    {
        if (field == null) return;
        field.SetTextWithoutNotify(FightSettings.ClampHealth(ReadHealth(field, 100) + change).ToString());
    }

    private void UpdateVolume(float value)
    {
        AudioListener.volume = value;
        RefreshSettings();
    }

    public void StartBattle()
    {
        if (!selection.IsSelecting || trainingPanel.activeSelf) return;
        ResetPresentation();
        SaveSettings();
        player.SetMaxHealth(FightSettings.PlayerHealth);
        selection.SelectEnemy(selectedEnemy);
        if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        RefreshHuds(true);
    }

    public void Retry()
    {
        ResetPresentation();
        player.ResetBattle();
        if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        view.result.SetActive(false);
        RefreshHuds(true);
    }

    private void StartTraining()
    {
        if (!selection.IsSelecting || trainingPanel.activeSelf) return;
        ResetPresentation();
        SaveSettings();
        PrepareTrainingBag();
        player.SetOpponent(trainingReference);
        player.TrainingMode = true;
        player.SetGameplayEnabled(true);
        if (trainingAttack == null)
        {
            GameObject source = new GameObject("TrainingAttackSource");
            trainingAttack = source.AddComponent<DilatantLiquidAttack>();
        }
        trainingAttack.transform.position = trainingReference.position;
        trainingAttack.ConfigureTraining(player, Resources.Load<Material>("FightArena/ArenaLit"));
        trainingAttack.gameObject.SetActive(true);
        trainingPunchTime = float.NegativeInfinity;
        trainingInstruction = "下のボタンで攻撃を選び、予測線を見て避けよう";
        view.setup.SetActive(false);
        trainingPanel.SetActive(true);
        UpdateTraining();
    }

    private void PrepareTrainingBag()
    {
        Vector3 floor = player.GetStandingPosition(0f);
        GameObject enemy = selection.GetEnemy(selectedEnemy);
        Vector3 direction = enemy != null ? Vector3.ProjectOnPlane(enemy.transform.position - floor, Vector3.up) : player.TrackingForwardDirection;
        if (direction.sqrMagnitude < 0.001f) direction = Vector3.forward;
        float height = Mathf.Max(0.6f, player.StandingBodyHeight);
        if (trainingReference == null)
        {
            trainingReference = new GameObject("TrainingPunchBag").transform;
            trainingBag = new GameObject("BagPivot").transform;
            trainingBag.SetParent(trainingReference, false);
            trainingBag.localPosition = Vector3.up * (height * 0.21f);
            CreateBagPart(trainingReference, "Base", PrimitiveType.Cylinder, new Vector3(0f, height * 0.055f, 0f),
                new Vector3(0.46f, 0.055f, 0.46f) * height, new Color(0.13f, 0.16f, 0.22f));
            CreateBagPart(trainingReference, "Support", PrimitiveType.Cylinder, new Vector3(0f, height * 0.21f, 0f),
                new Vector3(0.09f, 0.15f, 0.09f) * height, new Color(0.2f, 0.23f, 0.3f));
            CreateBagPart(trainingBag, "Cushion", PrimitiveType.Capsule, new Vector3(0f, height * 0.43f, 0f),
                new Vector3(0.34f, 0.42f, 0.34f) * height, new Color(0.8f, 0.13f, 0.19f));
            CreateBagPart(trainingBag, "Stripe", PrimitiveType.Cylinder, new Vector3(0f, height * 0.45f, 0f),
                new Vector3(0.345f, 0.025f, 0.345f) * height, new Color(1f, 0.72f, 0.26f));
        }
        trainingReference.SetPositionAndRotation(floor + direction.normalized * Mathf.Max(1.2f, height * 1.45f), Quaternion.LookRotation(direction));
        trainingBag.localRotation = Quaternion.identity;
        trainingReference.gameObject.SetActive(true);
    }

    private void CreateBagPart(Transform parent, string partName, PrimitiveType shape, Vector3 position, Vector3 scale, Color color)
    {
        GameObject part = GameObject.CreatePrimitive(shape);
        part.name = partName;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        Collider collider = part.GetComponent<Collider>();
        collider.enabled = false;
        Destroy(collider);
        Renderer renderer = part.GetComponent<Renderer>();
        Material material = Resources.Load<Material>("FightArena/ArenaLit");
        if (material != null) renderer.sharedMaterial = material;
        MaterialPropertyBlock tint = new MaterialPropertyBlock();
        tint.SetColor("_BaseColor", color);
        tint.SetColor("_Color", color);
        renderer.SetPropertyBlock(tint);
    }

    private void OnTrainingPunch()
    {
        if (trainingPanel == null || !trainingPanel.activeSelf) return;
        trainingPunchTime = Time.unscaledTime;
        trainingBag.localRotation = Quaternion.Euler(13f, 0f, 0f);
        BattleAudio.Play(FightSound.PlayerPunch);
    }

    private void StartTrainingAttack(DilatantLiquidAttack.AttackPattern pattern)
    {
        if (!trainingPanel.activeSelf || !trainingAttack.TryStartTrainingAttack(pattern)) return;
        switch (pattern)
        {
            case DilatantLiquidAttack.AttackPattern.FrontShot:
                trainingInstruction = "前方攻撃　縦線から左右に外れる";
                break;
            case DilatantLiquidAttack.AttackPattern.UpperWave:
                trainingInstruction = "波状攻撃　横線が見えたらしゃがむ";
                break;
            case DilatantLiquidAttack.AttackPattern.LiquidPunch:
                trainingInstruction = "近距離パンチ　下側の帯が見えたら後退する";
                break;
        }
    }

    private void UpdateTraining()
    {
        float elapsed = Time.unscaledTime - trainingPunchTime;
        float tilt = elapsed < 1.2f ? Mathf.Cos(elapsed * 15f) * Mathf.Exp(-elapsed * 4f) * 13f : 0f;
        trainingBag.localRotation = Quaternion.Euler(tilt, 0f, 0f);
        string input = player.HasLiveTracking ? "画像認識を受信中" : player.KeyboardTestEnabled ? "キーボード操作可" : "画像認識の入力待ち";
        trainingStatus.text = trainingInstruction + "　／　" + input;
    }

    private void OnDestroy()
    {
        if (player != null) player.PunchPressed -= OnTrainingPunch;
        if (trainingAttack != null) Destroy(trainingAttack.gameObject);
        if (player != null && trainingPanel != null && trainingPanel.activeSelf) player.TrainingMode = false;
        if (trainingReference != null) Destroy(trainingReference.gameObject);
    }

    public void ReturnToMenu()
    {
        if (trainingAttack != null) trainingAttack.gameObject.SetActive(false);
        player.TrainingMode = false;
        if (trainingPanel != null) trainingPanel.SetActive(false);
        if (trainingReference != null) trainingReference.gameObject.SetActive(false);
        ResetPresentation();
        SaveSettings();
        selection.ReturnToSelection();
        view.result.SetActive(false);
        RefreshHuds(true);
    }

    private void ResetPresentation()
    {
        resultAnnounced = false;
        resultElapsed = 0f;
        if (feedback != null) feedback.ResetEffects();
    }

    private void Update()
    {
        if (player == null || selection == null || view == null) return;
        if (trainingPanel != null && trainingPanel.activeSelf)
        {
            UpdateTraining();
            return;
        }
        RefreshHuds(false);
        bool finished = !selection.IsSelecting && (player.IsDead || (player.OpponentMaxHealth > 0f && player.OpponentHealth <= 0f));
        if (view.rush != null)
        {
            view.rush.SetActive(!selection.IsSelecting && player.IsFinisherRush);
            view.rushCount.text = "連打！ " + player.RushHits + " / " + player.RushGoal;
            view.rushTime.text = player.RushRemaining.ToString("0.0") + " 秒";
        }
        if (!finished)
        {
            if (resultAnnounced) ResetPresentation();
            view.result.SetActive(false);
            return;
        }
        if (!resultAnnounced)
        {
            resultAnnounced = true;
            resultElapsed = 0f;
            if (player.IsDead || !player.RushSucceeded) BattleAudio.Play(player.IsDead ? FightSound.Defeat : FightSound.Victory);
            if (!player.IsDead && feedback != null) feedback.PlayVictory();
        }
        resultElapsed += Time.unscaledDeltaTime;
        view.result.SetActive(player.IsDead || resultElapsed >= 1.1f);
        RefreshHuds(true);
        view.resultTitle.text = player.IsDead ? "DEFEAT" : "CLEAR!";
        view.resultTitle.color = player.IsDead ? EnemyColor : new Color(1f, 0.79f, 0.24f);
        view.resultDetail.text = player.IsDead ? "もう一度、タイミングをつかもう。" : (player.RushSucceeded ? "ラッシュ成功！\n" : "") + EnemyName(selection.SelectedEnemy) + " を撃破！";
    }

    private void RefreshHuds(bool immediate)
    {
        for (int hudIndex = 0; hudIndex < view.huds.Length; hudIndex++)
        {
            Hud hud = view.huds[hudIndex];
            hud.root.SetActive(!selection.IsSelecting && hudIndex == 0);
            if (selection.IsSelecting || hudIndex != 0) continue;
            float playerRatio = player.CurrentHealth / Mathf.Max(1f, player.MaxHealth);
            float enemyRatio = player.OpponentHealth / Mathf.Max(1f, player.OpponentMaxHealth);
            hud.playerFill.fillAmount = immediate ? playerRatio : Mathf.MoveTowards(hud.playerFill.fillAmount, playerRatio, Time.unscaledDeltaTime * 2f);
            hud.enemyFill.fillAmount = immediate ? enemyRatio : Mathf.MoveTowards(hud.enemyFill.fillAmount, enemyRatio, Time.unscaledDeltaTime * 2f);
            hud.playerFill.color = playerRatio > 0.3f ? Accent : new Color(1f, 0.65f, 0.15f);
            hud.playerValue.text = player.CurrentHealth.ToString("0") + " / " + player.MaxHealth.ToString("0");
            hud.enemyValue.text = player.OpponentHealth.ToString("0") + " / " + player.OpponentMaxHealth.ToString("0");
            hud.enemyName.text = EnemyName(selection.SelectedEnemy);
            hud.stage.text = "STAGE " + player.OpponentStage;
            hud.status.text = player.IsDead ? "敗北" : player.OpponentHealth <= 0f ? "勝利" : "";
            for (int index = 0; index < hud.phaseMarks.Length; index++) hud.phaseMarks[index].color = index < player.OpponentStage ? Accent : new Color(0.2f, 0.24f, 0.3f);
        }
    }

    private static string EnemyName(GameObject enemy)
    {
        if (enemy == null) return "ENEMY";
        if (enemy.GetComponentInChildren<HybridBossAttack>(true) != null) return "HYBRID OVERDRIVE";
        return enemy.GetComponentInChildren<DilatantEnemy>(true) != null ? "DILATANT SENTINEL" : "PUNCHBOT";
    }
}
